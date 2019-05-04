﻿using Mademy;
using Mademy.OpenCL;
using OpenCL.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Mademy.OpenCL.ComputeFramework;

namespace Mademy
{
    public class MathLib
    {
        private ComputeFramework computeFramework = null;
        private static readonly string calcLayerKernel = "calcSingleLayer";
        private static readonly string forwardPass = "trainingForwardPass";
        private static readonly string backwardPassKernel = "trainingBackwardPass";

        public MathLib(ComputeDevice clDevice = null)
        {
            if ( clDevice != null)
                computeFramework = new ComputeFramework(clDevice, new string[] { CLSourceProvider.ReadSourceFile() }, new string[] { calcLayerKernel, forwardPass, backwardPassKernel } , "-cl-finite-math-only -Werror");
        }

        /// <summary>
        /// Extends the global work size to the nearest upper multiple of the localSize
        /// </summary>
        /// <param name="desiredGlobalSize"></param>
        /// <param name="localSize"></param>
        /// <returns></returns>
        private int ExtendGlobalWorkSize(int desiredGlobalSize, int localSize)
        {
            return ((desiredGlobalSize % localSize) == 0) ? desiredGlobalSize : (desiredGlobalSize + (localSize - (desiredGlobalSize % localSize)));
        }

        private bool HasComputeFramework() { return computeFramework != null; }

        public MathLib Clone()
        {
            return new MathLib(HasComputeFramework() ? computeFramework.GetOpenCLDevice() : null);
        }

        internal float[] CalculateLayer(float[,] weightMx, float[] bias, float[] prevActivations, IActivationFunction sigmoidFunction)
        {
            if (!HasComputeFramework()) //CPU fallback
            {
                float[] ret = new float[weightMx.GetLength(0)];
                for (int m = 0; m < weightMx.GetLength(0); m++)
                {
                    float acc = 0.0f;
                    for (int k = 0; k < weightMx.GetLength(1); k++)
                    {
                        acc += weightMx[m, k] * prevActivations[k];
                    }
                    acc += bias[m];

                    ret[m] = sigmoidFunction.Calculate(acc);
                }
                return ret;
            }

            int matrixRows = weightMx.GetLength(0);

            MemoryAllocation mem_param_weightMx, mem_param_bias, mem_param_prevActivation, mem_param_config, mem_param_output;
            unsafe
            {
                fixed (float* weightArrayPtr = weightMx)
                {
                    mem_param_weightMx = computeFramework.GetMemoryFor(weightMx.Length * 4, MemFlags.ReadOnly | MemFlags.CopyHostPtr, new IntPtr(weightArrayPtr));
                }
                fixed (float* biasPtr = bias)
                {
                    mem_param_bias = computeFramework.GetMemoryFor(bias.Length * 4, MemFlags.ReadOnly | MemFlags.CopyHostPtr, new IntPtr(biasPtr));
                }
                fixed (float* prevActivationPtr = prevActivations)
                {
                    mem_param_prevActivation = computeFramework.GetMemoryFor(prevActivations.Length * 4, MemFlags.ReadOnly | MemFlags.CopyHostPtr, new IntPtr(prevActivationPtr));
                }

                int[] configParams = new int[] { /*rows: */weightMx.GetLength(0), /*cols: */weightMx.GetLength(1), /*ApplySigmoid*/ sigmoidFunction.GetOpenCLFunctionId() };
                fixed (int* configPtr = configParams)
                {
                    mem_param_config = computeFramework.GetMemoryFor(configParams.Length * 4, MemFlags.ReadOnly | MemFlags.CopyHostPtr, new IntPtr(configPtr));
                }
                mem_param_output = computeFramework.GetMemoryFor(matrixRows * 4, MemFlags.WriteOnly, IntPtr.Zero);
            }

            computeFramework.SetKernelArg(calcLayerKernel, 0, mem_param_weightMx);
            computeFramework.SetKernelArg(calcLayerKernel, 1, mem_param_bias);
            computeFramework.SetKernelArg(calcLayerKernel, 2, mem_param_prevActivation);
            computeFramework.SetKernelArg(calcLayerKernel, 3, mem_param_config);
            computeFramework.SetKernelArg(calcLayerKernel, 4, mem_param_output);

            int localWorkgroupSize = 32;
            int globalWorkSize = ExtendGlobalWorkSize(matrixRows, localWorkgroupSize);
            computeFramework.EnqueueKernel(calcLayerKernel, new IntPtr[] { new IntPtr(globalWorkSize) }, new IntPtr[] { new IntPtr(localWorkgroupSize) });

            float[] output = new float[matrixRows];

            unsafe
            {
                fixed (float* outputPtr = output)
                {
                    computeFramework.ReadBuffer(mem_param_output, true, IntPtr.Zero, new IntPtr(matrixRows * 4), new IntPtr(outputPtr));
                }
            }

            computeFramework.UnuseMemoryAllocations();

            return output;
        }

        private void CalculateGradientForSingleTrainingExample( Network network, IErrorFunction errorFunction, ref List<List<NeuronData>> intermediateResults, float[] trainingInput, float[] trainingDesiredOutput)
        {
            List<float[]> activations = new List<float[]>();
            List<float[]> zValues = new List<float[]>();
            network.Compute(this, trainingInput, ref activations, ref zValues, false); //dont flush working cache

            var lastLayerGradient = intermediateResults.Last();
            List<float> delta_k_holder = new List<float>();
            CalculateOutputLayerGradient(network, errorFunction, ref lastLayerGradient, ref delta_k_holder, activations, trainingInput, zValues, trainingDesiredOutput);

            for (int i = network.layers.Count - 2; i >= 0; --i)
            {
                var layerGradient = intermediateResults[i];
                CalculateHiddenLayerGradient(network, i, ref layerGradient, ref delta_k_holder, i == 0 ? trainingInput : activations[i - 1], zValues);
            }
        }

        private void CalculateOutputLayerGradient(Network network, IErrorFunction errorFunction, ref List<NeuronData> gradientData, ref List<float> delta_k_vector, List<float[]> activations, float[] trainingInput, List<float[]> zValues, float[] desiredOutput)
        {
            var prevActivations = activations.Count <= 1 ? trainingInput : activations[activations.Count - 2];
            int lastLayerWeightCount = network.layers.Last().GetWeightsPerNeuron();
            int lastLayerNeuronCount = network.layers.Last().GetNeuronCount();
            for (int i = 0; i < lastLayerNeuronCount; i++)
            {
                float outputValue = activations.Last()[i];
                float delta_k = errorFunction.CalculateDelta(zValues.Last()[i], outputValue, desiredOutput[i], network.activationFunction);

                var gradientDataItem = gradientData[i];
                //Assert(gradientData[i].weights.Length == prevActivations.Length);
                for (int j = 0; j < lastLayerWeightCount; j++)
                {
                    gradientDataItem.weights[j] += delta_k * prevActivations[j];
                }
                gradientDataItem.bias += delta_k;
                delta_k_vector.Add(delta_k);
            }
        }
        private void CalculateHiddenLayerGradient(Network network, int L, ref List<NeuronData> gradientData, ref List<float> delta_k_vector, float[] prevLayerActivations, List<float[]> zValues)
        {
            List<float> newGammak = new List<float>();
            int layerWeightCount = network.layers[L].GetWeightsPerNeuron();
            int layerNeuronCount = network.layers[L].GetNeuronCount();

            for (int i = 0; i < layerNeuronCount; i++)
            {
                float deltak = 0;
                //Assert(delta_k_vector.Count == layers[L + 1].weightMx.GetLength(0));
                for (int k = 0; k < delta_k_vector.Count; k++)
                {
                    deltak += delta_k_vector[k] * network.layers[L + 1].weightMx[k, i];
                }
                deltak *= network.activationFunction.CalculatePrime(zValues[L][i]);
                newGammak.Add(deltak);

                //Assert(gradientData[i].weights.Length == prevLayerActivations.Length);
                var gradientDataItem = gradientData[i];
                for (int j = 0; j < layerWeightCount; j++)
                {
                    gradientDataItem.weights[j] += deltak * (prevLayerActivations[j]);
                }
                gradientDataItem.bias += deltak;
            }

            delta_k_vector = newGammak;
        }

        internal void FlushWorkingCache()
        {
            if ( HasComputeFramework())
                computeFramework.FlushWorkingCache();
        }

        /// <summary>
        /// Runs backpropagation
        /// </summary>
        /// <param name="network"></param>
        /// <param name="suite"></param>
        /// <param name="trainingDataBegin"></param>
        /// <param name="trainingDataEnd"></param>
        /// <returns></returns>
        internal List<List<NeuronData>> CalculateAccumulatedGradientForMinibatch(Network network, TrainingSuite suite, int trainingDataBegin, int trainingDataEnd)
        {
            //Backpropagation
            var ret = Utils.CreateGradientVector(network);

            if (!HasComputeFramework()) //CPU fallback
            {
                for (int i = trainingDataBegin; i < trainingDataEnd; i++)
                {
                    CalculateGradientForSingleTrainingExample(network, suite.config.costFunction, ref ret, suite.trainingData[i].input, suite.trainingData[i].desiredOutput);
                }
                return ret;
            }

            //TODO run whole minibatch on the OpenCL device
            int trainingSamples = trainingDataEnd - trainingDataBegin;


            int[] networkConfigParams = null;
            int totalWeightAndBiasCount = 0;
            int widestLayerNeuronCount = 0;
            int totalActivationCount = 0; //Add 
            {
                foreach (var item in network.layers)
                {
                    totalActivationCount += item.GetNeuronCount();
                }

                List<int> networkConfigParamsList = new List<int>();
                /*0*/networkConfigParamsList.Add(0); //layer index to be processed
                /*1*/networkConfigParamsList.Add(network.layers.Count); //Layer count
                /*2*/networkConfigParamsList.Add(trainingSamples); //Layer count
                /*3*/networkConfigParamsList.Add(network.activationFunction.GetOpenCLFunctionId()); //Activation function
                /*4*/networkConfigParamsList.Add(suite.config.costFunction.GetOpenCLFunctionID()); //Cost function
                /*5*/networkConfigParamsList.Add(totalActivationCount); //totalActivationCount
                /*6*/networkConfigParamsList.Add(0); //totalWeightsAndBiases
                /*7*/networkConfigParamsList.Add(0); //widestLayerNeuronCount
                /*8*/networkConfigParamsList.Add(network.layers.First().GetWeightsPerNeuron()); //Input count
                for (int i = 0; i < network.layers.Count; i++)
                {
                    networkConfigParamsList.Add(network.layers[i].GetNeuronCount()); //Layer neuron count
                    totalWeightAndBiasCount += network.layers[i].biases.Length;
                    totalWeightAndBiasCount += network.layers[i].weightMx.Length;
                    widestLayerNeuronCount = Math.Max(network.layers[i].GetNeuronCount(), widestLayerNeuronCount);
                }

                networkConfigParamsList[6] = totalWeightAndBiasCount;
                networkConfigParamsList[7] = widestLayerNeuronCount;

                networkConfigParams = networkConfigParamsList.ToArray();
            }
            MemoryAllocation mem_NetworkConfigParams = computeFramework.GetMemoryFor( MemFlags.ReadOnly | MemFlags.CopyHostPtr, networkConfigParams );

            int inputActivationCount = network.layers.First().GetWeightsPerNeuron();
            float[] inputParameters = new float[trainingSamples * inputActivationCount];
            for (int i = 0; i < trainingSamples; ++i)
                Buffer.BlockCopy(suite.trainingData[i].input, 0, inputParameters, i * inputActivationCount * 4, inputActivationCount * 4);
            MemoryAllocation mem_InputActivations = computeFramework.GetMemoryFor(MemFlags.ReadOnly | MemFlags.CopyHostPtr, inputParameters);

            ///Contains the whole network's activation values, and Z values for each training sample
            ///Memory layout for one layer is like this: [...input values...][...first layer's activations...][...second layer's activations]...[last layer's activations][first layer's z values][second layer's zvalues]...[last layer's z values]
            ///After that, the next layer's same values are there
            MemoryAllocation mem_activationsAndZValues = computeFramework.GetMemoryFor(totalActivationCount * trainingSamples * 2 * 4, MemFlags.ReadWrite, IntPtr.Zero);


            float[] weightsAndBiases = new float[totalWeightAndBiasCount];
            {
                int offset = 0;
                foreach (var layer in network.layers)
                {
                    Buffer.BlockCopy(layer.weightMx, 0, weightsAndBiases, offset, layer.weightMx.Length * 4);
                    offset += layer.weightMx.Length * 4;
                    Buffer.BlockCopy(layer.biases, 0, weightsAndBiases, offset, layer.biases.Length * 4);
                    offset += layer.biases.Length * 4;
                }
            }
            MemoryAllocation mem_weightsAndBiases = computeFramework.GetMemoryFor(MemFlags.ReadOnly | MemFlags.CopyHostPtr, weightsAndBiases);

            //delta_k_vector is double buffered (hence the * 2). In a pass, the previous delta_k values are read, and the next ones are written
            //Memory layout is: [delta_k_vector buffer1 of trainingSample0][delta_k_vector buffer2 of trainingSample0] [delta_k_vector buffer1 of trainingSample1][delta_k_vector buffer2 of trainingSample1] ...
            MemoryAllocation mem_delta_k_vector = computeFramework.GetMemoryFor(widestLayerNeuronCount * trainingSamples * 2 * 4, MemFlags.ReadWrite, IntPtr.Zero );

            int[] layerIdUpdateSubbuffer = new int[] { 0 };
            computeFramework.SetKernelArg(forwardPass, 0, mem_NetworkConfigParams);
            computeFramework.SetKernelArg(forwardPass, 1, mem_activationsAndZValues);
            computeFramework.SetKernelArg(forwardPass, 2, mem_InputActivations);
            computeFramework.SetKernelArg(forwardPass, 3, mem_weightsAndBiases);

            var localWorkGroupSize = new IntPtr[] { new IntPtr(8), new IntPtr(8) };
            var globalWorkSize = new IntPtr[] { new IntPtr(0)
                , new IntPtr(ExtendGlobalWorkSize(trainingSamples, localWorkGroupSize[1].ToInt32())) };

            #region Forward pass
            for (int i = 0; i < network.layers.Count; i++)
            {
                if (i == 0)
                {
                    globalWorkSize[0] = new IntPtr(ExtendGlobalWorkSize(inputActivationCount, localWorkGroupSize[0].ToInt32()));
                }
                else
                {
                    layerIdUpdateSubbuffer[0] = i;
                    computeFramework.UploadToMemory(mem_NetworkConfigParams, 0, layerIdUpdateSubbuffer, true); //Update layer index to be processed by the kernel
                    globalWorkSize[0] = new IntPtr(ExtendGlobalWorkSize(network.layers[i-1].GetNeuronCount(), localWorkGroupSize[0].ToInt32()));
                }

                computeFramework.EnqueueKernel(forwardPass, globalWorkSize, localWorkGroupSize);
                // todo: run forward pass
            }
            #endregion

            /*{
                //DEBUG CODE, DELETE it
                float[] alma = new float[mem_activationsAndZValues.bufferSizeInBytes / 4];
                unsafe
                {
                    fixed (float* outputPtr = alma)
                    {
                        computeFramework.ReadBuffer(mem_activationsAndZValues, true, IntPtr.Zero, new IntPtr(mem_activationsAndZValues.bufferSizeInBytes), new IntPtr(outputPtr));
                    }
                }
                Console.WriteLine("sajt");

                float[] testdata = new float[300];
                var tempcomp = computeFramework;
                computeFramework = null;

                int j = 0;
                    List<float[]> preVAc = new List<float[]>();
                for (int l = 0; l < network.layers.Count; l++)
                {
                    for (int i = trainingDataBegin; i < trainingDataEnd; i++)
                    {
                        float[] result = CalculateLayer(network.layers[l].weightMx, network.layers[l].biases, l == 0 ? suite.trainingData[i].input : preVAc[i], network.activationFunction);
                        for (int k = 0; k < result.Length; k++)
                        {
                            testdata[j] = result[k];
                            j++;
                        }
                        preVAc.Add(result);
                    }
                    j += 50;
                }
                computeFramework = tempcomp;
                //debug code end
            }*/

            #region backward pass
            var mem_param_gradient = computeFramework.GetMemoryFor(totalWeightAndBiasCount * 4 * trainingSamples, MemFlags.WriteOnly, IntPtr.Zero);

            float[] desiredOutputs = new float[network.layers.Last().GetNeuronCount() * trainingSamples];
            int desiredOutputByteSizePerTrainigSample = network.layers.Last().GetNeuronCount() * 4;
            for (int i = 0; i < trainingSamples; i++)
                Buffer.BlockCopy(suite.trainingData[i].desiredOutput, 0, desiredOutputs, i * desiredOutputByteSizePerTrainigSample, desiredOutputByteSizePerTrainigSample);
            var mem_desired_outputs = computeFramework.GetMemoryFor(MemFlags.ReadOnly | MemFlags.CopyHostPtr, desiredOutputs);

            computeFramework.SetKernelArg(backwardPassKernel, 0, mem_NetworkConfigParams);
            computeFramework.SetKernelArg(backwardPassKernel, 1, mem_activationsAndZValues);
            computeFramework.SetKernelArg(backwardPassKernel, 2, mem_delta_k_vector);
            computeFramework.SetKernelArg(backwardPassKernel, 3, mem_param_gradient);
            computeFramework.SetKernelArg(backwardPassKernel, 4, mem_desired_outputs);
            computeFramework.SetKernelArg(backwardPassKernel, 5, mem_InputActivations);
            computeFramework.SetKernelArg(backwardPassKernel, 6, mem_weightsAndBiases);

            //Run backward pass for all hidden layers
            for (int i = network.layers.Count - 1; i >= 0; --i)
            {
                globalWorkSize[0] = new IntPtr(ExtendGlobalWorkSize(network.layers[i].GetNeuronCount(), localWorkGroupSize[0].ToInt32()));
                layerIdUpdateSubbuffer[0] = i;
                computeFramework.UploadToMemory(mem_NetworkConfigParams, 0, layerIdUpdateSubbuffer, true); //Update layer index to be processed by the kernel
                computeFramework.EnqueueKernel(backwardPassKernel, globalWorkSize, localWorkGroupSize);
            }
            #endregion

            float[] outputGradient = new float[mem_param_gradient.bufferSizeInBytes / 4];
            unsafe
            {
                fixed (float* outputPtr = outputGradient)
                {
                    computeFramework.ReadBuffer(mem_param_gradient, true, new IntPtr(0), new IntPtr(mem_param_gradient.bufferSizeInBytes), new IntPtr(outputPtr));
                }
            }

            computeFramework.UnuseMemoryAllocations();

            int gradIdx = 0;
            int gradArrayStride = totalWeightAndBiasCount;
            foreach (var layer in ret)
            {
                foreach (var neuron in layer)
                {
                    for (int i = 0; i < neuron.weights.Length; ++i)
                    {
                        for (int t = 0; t < trainingSamples; ++t)
                        {
                            neuron.weights[i] += outputGradient[gradIdx + gradArrayStride * t];
                        }
                        ++gradIdx;
                    }
                    neuron.bias = outputGradient[gradIdx];
                    ++gradIdx;
                }
            }

            return ret;
        }

    }
}
