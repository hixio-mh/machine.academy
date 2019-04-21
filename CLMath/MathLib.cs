﻿using OpenCL.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CLMath
{
    public class MathLib
    {
        Context clContext;
        ComputeDevice clDevice = null;
        CommandQueue commandQueue;
        Program clProgram;
        Dictionary<String, Kernel> kernels;
        bool hasClInitialized = false;

        public MathLib(ComputeDevice clDevice)
        {
            this.clDevice = clDevice;
            InitCL();
        }

        public static float Sigmoid(float x)
        {
            return x / (1.0f + Math.Abs(x));
        }

        public static float SigmoidPrime(float x)
        {
            float div = (1.0f + Math.Abs(x));
            return 1 / (div * div);
        }

        private void CleanupCLResources()
        {
            if (hasClInitialized)
            {
                foreach (var item in kernels)
                {
                    Cl.ReleaseKernel(item.Value);
                }
                kernels.Clear();
                Cl.ReleaseProgram(clProgram);
                Cl.ReleaseCommandQueue(commandQueue);
                Cl.ReleaseContext(clContext);
                hasClInitialized = false;
            }
        }

        private void InitCL()
        {
            if (!hasClInitialized && clDevice != null)
            {
                /*
                ErrorCode err;
                ContextProperty[] contextProps = new ContextProperty[] { new ContextProperty(ContextProperties.Platform, (IntPtr)clDevice.GetPlatformID()) };
                clContext = Cl.CreateContext(contextProps, 1, new Device[] { clDevice.GetDevice() }, null, IntPtr.Zero, out err);
                if (err != ErrorCode.Success) throw new Exception("Failed to create context! " + err.ToString());

                commandQueue = Cl.CreateCommandQueue(clContext, clDevice.GetDevice(), CommandQueueProperties.None, out err);
                if (err != ErrorCode.Success) throw new Exception("Failed to create command queue! " + err.ToString());

                clProgram = Cl.CreateProgramWithSource(clContext, 1, new string[] { "clsrc" }, null, out err);
                if (err != ErrorCode.Success) throw new Exception("Failed to create program! " + err.ToString());

                Cl.BuildProgram(clProgram, 1, new Device[] { clDevice.GetDevice() }, "", null, IntPtr.Zero);
                if (err != ErrorCode.Success)
                {
                    //TODO print build log!
                    throw new Exception("Failed to build program! " + err.ToString());
                }

                kernels["mxMul"] = Cl.CreateKernel(clProgram, "mxMul", out err);
                if (err != ErrorCode.Success) throw new Exception("Failed to create compute kernel! " + err.ToString());

                hasClInitialized = true;
                */
            }
        }

        public float[] CalculateLayer(float[,] weightMx, float[] bias, float[] prevActivations)
        {
            if (weightMx.GetLength(1) != prevActivations.GetLength(0))
                throw new Exception("Invalid input");

            //if (clDevice == null)
            {
                float[] ret = new float[weightMx.GetLength(0)];
                for (int m = 0; m < weightMx.GetLength(0); m++)
                {
                    float acc = 0.0f;
                    for (int k = 0; k < weightMx.GetLength(1); k++)
                    {
                        acc += weightMx[m, k] * prevActivations[k];
                    }
                    ret[m] = Sigmoid(acc + bias[m]);
                }
                return ret;
            }
            return null;

            /*int[] sizes = new int[] { size1, size2, size3 };

            InitCL();

            ErrorCode err;
            var mem_param_A = Cl.CreateBuffer<float>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, A, out err);
            var mem_param_B = Cl.CreateBuffer<float>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, B, out err);
            var mem_param_sizes = Cl.CreateBuffer<int>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, sizes, out err);
            */

        }

        public float[] CalculateZ(float[,] weightMx, float[] bias, float[] prevActivations)
        {
            if (weightMx.GetLength(1) != prevActivations.GetLength(0) || weightMx.GetLength(0) != bias.Length) 
                throw new Exception("Invalid input");

            //if (clDevice == null)
            {
                float[] ret = new float[weightMx.GetLength(0)];
                for (int m = 0; m < weightMx.GetLength(0); m++)
                {
                    float acc = 0.0f;
                    for (int k = 0; k < weightMx.GetLength(1); k++)
                    {
                        acc += weightMx[m, k] * prevActivations[k];
                    }
                    ret[m] = acc + bias[m];
                }
                return ret;
            }
            return null;

            /*int[] sizes = new int[] { size1, size2, size3 };

            InitCL();

            ErrorCode err;
            var mem_param_A = Cl.CreateBuffer<float>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, A, out err);
            var mem_param_B = Cl.CreateBuffer<float>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, B, out err);
            var mem_param_sizes = Cl.CreateBuffer<int>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, sizes, out err);
            */

        }

        public float[] MatrixVecMul(float[,] A, float[] B)
        {
            if (A.GetLength(1) != B.GetLength(0))
                throw new Exception("Invalid input");

            //if (clDevice == null)
            {
                float[] ret = new float[A.GetLength(0)];
                for (int m = 0; m < A.GetLength(0); m++)
                {
                    float acc = 0.0f;
                    for (int k = 0; k < A.GetLength(1); k++)
                    {
                        acc += A[m, k] * B[k];
                    }
                    ret[m] = acc;
                }
                return ret;
            }
            return null;

            /*int[] sizes = new int[] { size1, size2, size3 };

            InitCL();

            ErrorCode err;
            var mem_param_A = Cl.CreateBuffer<float>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, A, out err);
            var mem_param_B = Cl.CreateBuffer<float>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, B, out err);
            var mem_param_sizes = Cl.CreateBuffer<int>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, sizes, out err);
            */

        }


        public float[,] MatrixMul(float[,] A, float[,] B)
        {
            if (A.GetLength(1) != B.GetLength(0))
                throw new Exception("Invalid input");

            //if (clDevice == null)
            {
                float[,] ret = new float[A.GetLength(0), B.GetLength(1)];
                for (int m = 0; m < A.GetLength(0); m++)
                {
                    for (int n = 0; n < B.GetLength(1); n++)
                    {
                        float acc = 0.0f;
                        for (int k = 0; k < A.GetLength(1); k++)
                        {
                            acc += A[m, k] * B[k,n];
                        }
                        ret[m, n] = acc;
                    }
                }
                return ret;
            }
            return null;

            /*int[] sizes = new int[] { size1, size2, size3 };

            InitCL();

            ErrorCode err;
            var mem_param_A = Cl.CreateBuffer<float>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, A, out err);
            var mem_param_B = Cl.CreateBuffer<float>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, B, out err);
            var mem_param_sizes = Cl.CreateBuffer<int>(clContext, MemFlags.ReadOnly | MemFlags.CopyHostPtr, sizes, out err);
            */

        }
    }
}
