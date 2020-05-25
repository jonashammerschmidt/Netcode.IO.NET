﻿using System;
using System.Collections;

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Crypto.Utilities;

namespace Org.BouncyCastle.Crypto.Engines
{
    /**
    * implementation of DSTU 7624 (Kalyna)
    */
    public class Dstu7624Engine
         : IBlockCipher
    {
        private static readonly int BITS_IN_WORD = 64;
        private static readonly int BITS_IN_BYTE = 8;

        private static readonly int REDUCTION_POLYNOMIAL = 0x011d; /* x^8 + x^4 + x^3 + x^2 + 1 */

        private ulong[] internalState;
        private ulong[] workingKey;
        private ulong[][] roundKeys;

        /* Number of 64-bit words in block */
        private int wordsInBlock;

        /* Number of 64-bit words in key */
        private int wordsInKey;

        /* Number of encryption rounds depending on key length */
        private static int ROUNDS_128 = 10;
        private static int ROUNDS_256 = 14;
        private static int ROUNDS_512 = 18;

        private int blockSizeBits;
        private int roundsAmount;

        private bool forEncryption;

        private byte[] internalStateBytes;
        private byte[] tempInternalStateBytes;

        public Dstu7624Engine(int blockSizeBits)
        {
            /* DSTU7624 supports 128 | 256 | 512 key/block sizes */
            if (blockSizeBits != 128 && blockSizeBits != 256 && blockSizeBits != 512)
            {
                throw new ArgumentException("Unsupported block length: only 128/256/512 are allowed");
            }
            this.blockSizeBits = blockSizeBits;

            wordsInBlock = blockSizeBits / BITS_IN_WORD;
            internalState = new ulong[wordsInBlock];

            internalStateBytes = new byte[internalState.Length * 64 / BITS_IN_BYTE];
            tempInternalStateBytes = new byte[internalState.Length * 64 / BITS_IN_BYTE];
        }

        #region INITIALIZATION
        public virtual void Init(bool forEncryption, ICipherParameters parameters)
        {
            if (parameters is KeyParameter)
            {
                this.forEncryption = forEncryption;

                byte[] keyBytes = ((KeyParameter)parameters).GetKey();
                int keyBitLength = keyBytes.Length * BITS_IN_BYTE;
                int blockBitLength = wordsInBlock * BITS_IN_WORD;

                if (keyBitLength != 128 && keyBitLength != 256 && keyBitLength != 512)
                {
                    throw new ArgumentException("unsupported key length: only 128/256/512 are allowed");
                }

                /* Limitations on key lengths depending on block lengths. See table 6.1 in standard */
                if (blockBitLength == 128)
                {
                    if (keyBitLength == 512)
                    {
                        throw new ArgumentException("Unsupported key length");
                    }
                }

                if (blockBitLength == 256)
                {
                    if (keyBitLength == 128)
                    {
                        throw new ArgumentException("Unsupported key length");
                    }
                }

                if (blockBitLength == 512)
                {
                    if (keyBitLength != 512)
                    {
                        throw new ArgumentException("Unsupported key length");
                    }
                }

                switch (keyBitLength)
                {
                    case 128:
                        roundsAmount = ROUNDS_128;
                        break;
                    case 256:
                        roundsAmount = ROUNDS_256;
                        break;
                    case 512:
                        roundsAmount = ROUNDS_512;
                        break;
                }

                wordsInKey = keyBitLength / BITS_IN_WORD;

                /* +1 round key as defined in standard */
                roundKeys = new ulong[roundsAmount + 1][];
                for (int roundKeyIndex = 0; roundKeyIndex < roundKeys.Length; roundKeyIndex++)
                {
                    roundKeys[roundKeyIndex] = new ulong[wordsInBlock];
                }

                workingKey = new ulong[wordsInKey];

                if (keyBytes.Length != wordsInKey * BITS_IN_WORD / BITS_IN_BYTE)
                {
                    throw new ArgumentException("Invalid key parameter passed to DSTU7624Engine init");
                }

                /* Unpack encryption key bytes to words */
                Pack.LE_To_UInt64(keyBytes, 0, workingKey);

                ulong[] kt = new ulong[wordsInBlock];

                KeyExpandKT(workingKey, kt);

                KeyExpandEven(workingKey, kt);

                KeyExpandOdd();

            }
            else if (parameters != null)
            {
                throw new ArgumentException("invalid parameter passed to Dstu7624 init - "
                + Platform.GetTypeName(parameters));
            }

            this.forEncryption = forEncryption;
        }

        private void KeyExpandKT(ulong[] key, ulong[] kt)
        {
            ulong[] k0 = new ulong[wordsInBlock];
            ulong[] k1 = new ulong[wordsInBlock];

            internalState = new ulong[wordsInBlock];
            internalState[0] += (ulong)(wordsInBlock + wordsInKey + 1);

            if (wordsInBlock == wordsInKey)
            {
                Array.Copy(key, k0, k0.Length);
                Array.Copy(key, k1, k1.Length);
            }
            else
            {
                Array.Copy(key, 0, k0, 0, wordsInBlock);
                Array.Copy(key, wordsInBlock, k1, 0, wordsInBlock);
            }

            AddRoundKeyExpand(k0);

            EncryptionRound();

            XorRoundKeyExpand(k1);

            EncryptionRound();

            AddRoundKeyExpand(k0);

            EncryptionRound();

            Array.Copy(internalState, kt, wordsInBlock);
        }

        private void KeyExpandEven(ulong[] key, ulong[] kt)
        {
            ulong[] initial_data = new ulong[wordsInKey];

            ulong[] kt_round = new ulong[wordsInBlock];

            ulong[] tmv = new ulong[wordsInBlock];

            int round = 0;

            Array.Copy(key, initial_data, wordsInKey);

            for (int i = 0; i < wordsInBlock; i++)
            {
                tmv[i] = 0x0001000100010001;
            }

            while (true)
            {
                Array.Copy(kt, internalState, wordsInBlock);

                AddRoundKeyExpand(tmv);

                Array.Copy(internalState, kt_round, wordsInBlock);
                Array.Copy(initial_data, internalState, wordsInBlock);

                AddRoundKeyExpand(kt_round);

                EncryptionRound();

                XorRoundKeyExpand(kt_round);

                EncryptionRound();

                AddRoundKeyExpand(kt_round);

                Array.Copy(internalState, roundKeys[round], wordsInBlock);

                if (roundsAmount == round)
                {
                    break;
                }
                if (wordsInKey != wordsInBlock)
                {
                    round += 2;

                    ShiftLeft(tmv);

                    Array.Copy(kt, internalState, wordsInBlock);

                    AddRoundKeyExpand(tmv);

                    Array.Copy(internalState, kt_round, wordsInBlock);
                    Array.Copy(initial_data, wordsInBlock, internalState, 0, wordsInBlock);

                    AddRoundKeyExpand(kt_round);

                    EncryptionRound();

                    XorRoundKeyExpand(kt_round);

                    EncryptionRound();

                    AddRoundKeyExpand(kt_round);

                    Array.Copy(internalState, roundKeys[round], wordsInBlock);

                    if (roundsAmount == round)
                    {
                        break;
                    }
                }

                round += 2;
                ShiftLeft(tmv);

                //Rotate initial data array on 1 element left
                ulong temp = initial_data[0];
                Array.Copy(initial_data, 1, initial_data, 0, initial_data.Length - 1);
                initial_data[initial_data.Length - 1] = temp;
            }
        }
        private void KeyExpandOdd()
        {
            for (int i = 1; i < roundsAmount; i += 2)
            {
                Array.Copy(roundKeys[i - 1], roundKeys[i], wordsInBlock);
                RotateLeft(roundKeys[i]);
            }
        }
        #endregion


        public virtual int ProcessBlock(byte[] input, int inOff, byte[] output, int outOff)
        {
            if (workingKey == null)
                throw new InvalidOperationException("Dstu7624 engine not initialised");

            Check.DataLength(input, inOff, GetBlockSize(), "input buffer too short");
            Check.OutputLength(output, outOff, GetBlockSize(), "output buffer too short");

            if (forEncryption)
            {
                Encrypt(input, inOff, output, outOff);
            }
            else
            {
                Decrypt(input, inOff, output, outOff);
            }

            return GetBlockSize();
        }

        private void Encrypt(byte[] plain, int inOff, byte[] cipherText, int outOff)
        {
            int round = 0;

            Array.Copy(plain, inOff, plain, 0, blockSizeBits / BITS_IN_BYTE);
            Array.Resize(ref plain, blockSizeBits / BITS_IN_BYTE);
            
            ulong[] plain_ = BytesToWords(plain);

            Array.Copy(plain_, internalState, wordsInBlock);

            AddRoundKey(round);

            for (round = 1; round < roundsAmount; round++)
            {
                EncryptionRound();

                XorRoundKey(round);

            }
            EncryptionRound();

            AddRoundKey(roundsAmount);

            ulong[] cipherText_ = new ulong[internalState.Length];

            Array.Copy(internalState, cipherText_, wordsInBlock);

            byte[] temp = WordsToBytes(cipherText_);

            Array.Copy(temp, 0, cipherText, outOff, temp.Length);

        }
        private void Decrypt(byte[] cipherText, int inOff, byte[] decryptedText, int outOff)
        {
            Array.Copy(cipherText, inOff, cipherText, 0, blockSizeBits / BITS_IN_BYTE);
            Array.Resize(ref cipherText, blockSizeBits / BITS_IN_BYTE);

            int round = roundsAmount;

            ulong[] cipherText_ = BytesToWords(cipherText);

            Array.Copy(cipherText_, internalState, wordsInBlock);

            SubRoundKey(round);

            for (round = roundsAmount - 1; round > 0; round--)
            {
                DecryptionRound();
                XorRoundKey(round);
            }

            DecryptionRound();
            SubRoundKey(0);

            ulong[] decryptedText_ = new ulong[internalState.Length];

            Array.Copy(internalState, decryptedText_, wordsInBlock);


            byte[] temp = WordsToBytes(decryptedText_);
            Array.Copy(temp, 0, decryptedText, outOff, temp.Length);

            
        }









        private void AddRoundKeyExpand(ulong[] value)
        {
            for (int i = 0; i < wordsInBlock; i++)
            {
                internalState[i] += value[i];
            }
        }

        private void EncryptionRound()
        {
            SubBytes();
            ShiftRows();
            MixColumns();
        }

        private void DecryptionRound()
        {
            InvMixColumns();
            InvShiftRows();
            InvSubBytes();
        }

        private void RotateLeft(ulong[] state_value)
        {
            int rotateBytesLength = 2 * state_value.Length + 3;
            int bytesLength = state_value.Length * (BITS_IN_WORD / BITS_IN_BYTE);


            byte[] bytes = WordsToBytes(state_value);
            byte[] buffer = new byte[rotateBytesLength];

            Array.Copy(bytes, buffer, rotateBytesLength);

            Buffer.BlockCopy(bytes, rotateBytesLength, bytes, 0, bytesLength - rotateBytesLength);

            Array.Copy(buffer, 0, bytes, bytesLength - rotateBytesLength, rotateBytesLength);

            var temp = BytesToWords(bytes);
            Array.Copy(temp, state_value, state_value.Length);
        }

        private void ShiftLeft(ulong[] state_value)
        {
            for (int i = 0; i < state_value.Length; i++)
            {
                state_value[i] <<= 1;
            }
            Array.Reverse(state_value);
        }

        private void XorRoundKeyExpand(ulong[] value)
        {
            for (int i = 0; i < wordsInBlock; i++)
            {
                internalState[i] ^= value[i];
            }
        }

        private void XorRoundKey(int round)
        {
            for (int i = 0; i < wordsInBlock; i++)
            {
                internalState[i] ^= roundKeys[round][i];
            }
        }

        private void ShiftRows()
        {
            int row, col;
            int shift = -1;

            byte[] stateBytes = WordsToBytes(internalState);

            byte[] nstate = new byte[wordsInBlock * sizeof(ulong)];

            for (row = 0; row < sizeof(ulong); row++)
            {
                if (row % (sizeof(ulong) / wordsInBlock) == 0)
                {
                    shift += 1;
                }

                for (col = 0; col < wordsInBlock; col++)
                {
                    nstate[row + ((col + shift) % wordsInBlock) * sizeof(ulong)] = stateBytes[row + col * sizeof(ulong)];
                }
            }

            internalState = BytesToWords(nstate);

        }

        private void InvShiftRows()
        {
            int row, col;
            int shift = -1;

            byte[] stateBytes = WordsToBytes(internalState);
            byte[] nstate = new byte[wordsInBlock * sizeof(ulong)];

            for (row = 0; row < sizeof(ulong); row++)
            {
                if (row % (sizeof(ulong) / wordsInBlock) == 0)
                {
                    shift += 1;
                }

                for (col = 0; col < wordsInBlock; col++)
                {
                    nstate[row + col * sizeof(ulong)] = stateBytes[row + ((col + shift) % wordsInBlock) * sizeof(ulong)];
                }
            }

            internalState = BytesToWords(nstate);
        }

        private ulong[] BytesToWords(byte[] bytes)
        {
            ulong[] words = new ulong[bytes.Length / sizeof(ulong)];

            for (int i = 0; i < words.Length; i++)
            {
                words[i] = BitConverter.ToUInt64(bytes, i * sizeof(ulong));

                if (!BitConverter.IsLittleEndian)
                {
                    words[i] = ReverseWord(words[i]);
                }
            }

            return words;
        }

        private byte[] WordsToBytes(ulong[] words)
        {
            byte[] bytes = new byte[words.Length * sizeof(ulong)];

            byte[] tempBytes = new byte[sizeof(ulong)];

            for (int i = 0; i < words.Length; ++i)
            {
                if (!BitConverter.IsLittleEndian)
                {
                    words[i] = ReverseWord(words[i]);
                }

                tempBytes = BitConverter.GetBytes(words[i]);
                Array.Copy(tempBytes, 0, bytes, i * tempBytes.Length, tempBytes.Length);
            }
            return bytes;
        }

        private ulong ReverseWord(ulong x)
        {
            byte[] bytes = BitConverter.GetBytes(x);
            Array.Reverse(bytes);
            return BitConverter.ToUInt64(bytes, 0);
        }

        private void AddRoundKey(int round)
        {
            for (int i = 0; i < wordsInBlock; ++i)
            {
                internalState[i] += roundKeys[round][i];
            }
        }

        private void SubRoundKey(int round)
        {
            for (int i = 0; i < wordsInBlock; ++i)
            {
                internalState[i] -= roundKeys[round][i];
            }
        }

        private void MixColumns()
        {
            MatrixMultiply(mdsMatrix);
        }

        private void InvMixColumns()
        {
            MatrixMultiply(mdsInvMatrix);
        }

        private void MatrixMultiply(byte[][] matrix)
        {
            int col, row, b;
            byte product;
            ulong result;
            byte[] stateBytes = WordsToBytes(internalState);

            for (col = 0; col < wordsInBlock; ++col)
            {
                result = 0;
                for (row = sizeof(ulong) - 1; row >= 0; --row)
                {
                    product = 0;
                    for (b = sizeof(ulong) - 1; b >= 0; --b)
                    {
                        product ^= MultiplyGF(stateBytes[b + col * sizeof(ulong)], matrix[row][b]);
                    }
                    result |= (ulong)product << (row * sizeof(ulong));
                }
                internalState[col] = result;
            }
        }

        private byte MultiplyGF(byte x, byte y)
        {
            byte r = 0;
            byte hbit = 0;

            for (int i = 0; i < BITS_IN_BYTE; i++)
            {
                if ((y & 0x01) == 1)
                {
                    r ^= x;
                }

                hbit = (byte)(x & 0x80);

                x <<= 1;

                if (hbit == 0x80)
                {
                    x = (byte)((int)x ^ REDUCTION_POLYNOMIAL);
                }
                y >>= 1;
            }
            return r;
        }

        private void SubBytes()
        {
            for (int i = 0; i < wordsInBlock; i++)
            {
                internalState[i] = sboxesForEncryption[0][internalState[i] & 0x00000000000000FF] |
                           ((ulong)sboxesForEncryption[1][(internalState[i] & 0x000000000000FF00) >> 8] << 8) |
                           ((ulong)sboxesForEncryption[2][(internalState[i] & 0x0000000000FF0000) >> 16] << 16) |
                           ((ulong)sboxesForEncryption[3][(internalState[i] & 0x00000000FF000000) >> 24] << 24) |
                           ((ulong)sboxesForEncryption[0][(internalState[i] & 0x000000FF00000000) >> 32] << 32) |
                           ((ulong)sboxesForEncryption[1][(internalState[i] & 0x0000FF0000000000) >> 40] << 40) |
                           ((ulong)sboxesForEncryption[2][(internalState[i] & 0x00FF000000000000) >> 48] << 48) |
                           ((ulong)sboxesForEncryption[3][(internalState[i] & 0xFF00000000000000) >> 56] << 56);
            }
        }

        private void InvSubBytes()
        {
            for (int i = 0; i < wordsInBlock; i++)
            {
                internalState[i] = sboxesForDecryption[0][internalState[i] & 0x00000000000000FF] |
                           ((ulong)sboxesForDecryption[1][(internalState[i] & 0x000000000000FF00) >> 8] << 8) |
                           ((ulong)sboxesForDecryption[2][(internalState[i] & 0x0000000000FF0000) >> 16] << 16) |
                           ((ulong)sboxesForDecryption[3][(internalState[i] & 0x00000000FF000000) >> 24] << 24) |
                           ((ulong)sboxesForDecryption[0][(internalState[i] & 0x000000FF00000000) >> 32] << 32) |
                           ((ulong)sboxesForDecryption[1][(internalState[i] & 0x0000FF0000000000) >> 40] << 40) |
                           ((ulong)sboxesForDecryption[2][(internalState[i] & 0x00FF000000000000) >> 48] << 48) |
                           ((ulong)sboxesForDecryption[3][(internalState[i] & 0xFF00000000000000) >> 56] << 56);
            }
        }


        #region TABLES AND S-BOXES

        private byte[][] mdsMatrix = 
          {
               new byte[] { 0x01, 0x01, 0x05, 0x01, 0x08, 0x06, 0x07, 0x04 },
               new byte[] { 0x04, 0x01, 0x01, 0x05, 0x01, 0x08, 0x06, 0x07 },
               new byte[] { 0x07, 0x04, 0x01, 0x01, 0x05, 0x01, 0x08, 0x06 },
               new byte[] { 0x06, 0x07, 0x04, 0x01, 0x01, 0x05, 0x01, 0x08 },
               new byte[] { 0x08, 0x06, 0x07, 0x04, 0x01, 0x01, 0x05, 0x01 },
               new byte[] { 0x01, 0x08, 0x06, 0x07, 0x04, 0x01, 0x01, 0x05 },
               new byte[] { 0x05, 0x01, 0x08, 0x06, 0x07, 0x04, 0x01, 0x01 },
               new byte[] { 0x01, 0x05, 0x01, 0x08, 0x06, 0x07, 0x04, 0x01 },
          };

        private byte[][] mdsInvMatrix = 
          {
               new byte[] { 0xAD, 0x95, 0x76, 0xA8, 0x2F, 0x49, 0xD7, 0xCA },
               new byte[] { 0xCA, 0xAD, 0x95, 0x76, 0xA8, 0x2F, 0x49, 0xD7 },
               new byte[] { 0xD7, 0xCA, 0xAD, 0x95, 0x76, 0xA8, 0x2F, 0x49 },
               new byte[] { 0x49, 0xD7, 0xCA, 0xAD, 0x95, 0x76, 0xA8, 0x2F },
               new byte[] { 0x2F, 0x49, 0xD7, 0xCA, 0xAD, 0x95, 0x76, 0xA8 },
               new byte[] { 0xA8, 0x2F, 0x49, 0xD7, 0xCA, 0xAD, 0x95, 0x76 },
               new byte[] { 0x76, 0xA8, 0x2F, 0x49, 0xD7, 0xCA, 0xAD, 0x95 },
               new byte[] { 0x95, 0x76, 0xA8, 0x2F, 0x49, 0xD7, 0xCA, 0xAD },
          };


        private byte[][] sboxesForEncryption = 
          {
               new byte[]
               {
                    0xa8, 0x43, 0x5f, 0x06, 0x6b, 0x75, 0x6c, 0x59, 0x71, 0xdf, 0x87, 0x95, 0x17, 0xf0, 0xd8, 0x09, 
                    0x6d, 0xf3, 0x1d, 0xcb, 0xc9, 0x4d, 0x2c, 0xaf, 0x79, 0xe0, 0x97, 0xfd, 0x6f, 0x4b, 0x45, 0x39, 
                    0x3e, 0xdd, 0xa3, 0x4f, 0xb4, 0xb6, 0x9a, 0x0e, 0x1f, 0xbf, 0x15, 0xe1, 0x49, 0xd2, 0x93, 0xc6, 
                    0x92, 0x72, 0x9e, 0x61, 0xd1, 0x63, 0xfa, 0xee, 0xf4, 0x19, 0xd5, 0xad, 0x58, 0xa4, 0xbb, 0xa1, 
                    0xdc, 0xf2, 0x83, 0x37, 0x42, 0xe4, 0x7a, 0x32, 0x9c, 0xcc, 0xab, 0x4a, 0x8f, 0x6e, 0x04, 0x27, 
                    0x2e, 0xe7, 0xe2, 0x5a, 0x96, 0x16, 0x23, 0x2b, 0xc2, 0x65, 0x66, 0x0f, 0xbc, 0xa9, 0x47, 0x41, 
                    0x34, 0x48, 0xfc, 0xb7, 0x6a, 0x88, 0xa5, 0x53, 0x86, 0xf9, 0x5b, 0xdb, 0x38, 0x7b, 0xc3, 0x1e, 
                    0x22, 0x33, 0x24, 0x28, 0x36, 0xc7, 0xb2, 0x3b, 0x8e, 0x77, 0xba, 0xf5, 0x14, 0x9f, 0x08, 0x55, 
                    0x9b, 0x4c, 0xfe, 0x60, 0x5c, 0xda, 0x18, 0x46, 0xcd, 0x7d, 0x21, 0xb0, 0x3f, 0x1b, 0x89, 0xff, 
                    0xeb, 0x84, 0x69, 0x3a, 0x9d, 0xd7, 0xd3, 0x70, 0x67, 0x40, 0xb5, 0xde, 0x5d, 0x30, 0x91, 0xb1, 
                    0x78, 0x11, 0x01, 0xe5, 0x00, 0x68, 0x98, 0xa0, 0xc5, 0x02, 0xa6, 0x74, 0x2d, 0x0b, 0xa2, 0x76, 
                    0xb3, 0xbe, 0xce, 0xbd, 0xae, 0xe9, 0x8a, 0x31, 0x1c, 0xec, 0xf1, 0x99, 0x94, 0xaa, 0xf6, 0x26, 
                    0x2f, 0xef, 0xe8, 0x8c, 0x35, 0x03, 0xd4, 0x7f, 0xfb, 0x05, 0xc1, 0x5e, 0x90, 0x20, 0x3d, 0x82, 
                    0xf7, 0xea, 0x0a, 0x0d, 0x7e, 0xf8, 0x50, 0x1a, 0xc4, 0x07, 0x57, 0xb8, 0x3c, 0x62, 0xe3, 0xc8, 
                    0xac, 0x52, 0x64, 0x10, 0xd0, 0xd9, 0x13, 0x0c, 0x12, 0x29, 0x51, 0xb9, 0xcf, 0xd6, 0x73, 0x8d, 
                    0x81, 0x54, 0xc0, 0xed, 0x4e, 0x44, 0xa7, 0x2a, 0x85, 0x25, 0xe6, 0xca, 0x7c, 0x8b, 0x56, 0x80 
               },

               new byte[]
               {
                    0xce, 0xbb, 0xeb, 0x92, 0xea, 0xcb, 0x13, 0xc1, 0xe9, 0x3a, 0xd6, 0xb2, 0xd2, 0x90, 0x17, 0xf8, 
                    0x42, 0x15, 0x56, 0xb4, 0x65, 0x1c, 0x88, 0x43, 0xc5, 0x5c, 0x36, 0xba, 0xf5, 0x57, 0x67, 0x8d, 
                    0x31, 0xf6, 0x64, 0x58, 0x9e, 0xf4, 0x22, 0xaa, 0x75, 0x0f, 0x02, 0xb1, 0xdf, 0x6d, 0x73, 0x4d, 
                    0x7c, 0x26, 0x2e, 0xf7, 0x08, 0x5d, 0x44, 0x3e, 0x9f, 0x14, 0xc8, 0xae, 0x54, 0x10, 0xd8, 0xbc, 
                    0x1a, 0x6b, 0x69, 0xf3, 0xbd, 0x33, 0xab, 0xfa, 0xd1, 0x9b, 0x68, 0x4e, 0x16, 0x95, 0x91, 0xee, 
                    0x4c, 0x63, 0x8e, 0x5b, 0xcc, 0x3c, 0x19, 0xa1, 0x81, 0x49, 0x7b, 0xd9, 0x6f, 0x37, 0x60, 0xca, 
                    0xe7, 0x2b, 0x48, 0xfd, 0x96, 0x45, 0xfc, 0x41, 0x12, 0x0d, 0x79, 0xe5, 0x89, 0x8c, 0xe3, 0x20, 
                    0x30, 0xdc, 0xb7, 0x6c, 0x4a, 0xb5, 0x3f, 0x97, 0xd4, 0x62, 0x2d, 0x06, 0xa4, 0xa5, 0x83, 0x5f, 
                    0x2a, 0xda, 0xc9, 0x00, 0x7e, 0xa2, 0x55, 0xbf, 0x11, 0xd5, 0x9c, 0xcf, 0x0e, 0x0a, 0x3d, 0x51, 
                    0x7d, 0x93, 0x1b, 0xfe, 0xc4, 0x47, 0x09, 0x86, 0x0b, 0x8f, 0x9d, 0x6a, 0x07, 0xb9, 0xb0, 0x98, 
                    0x18, 0x32, 0x71, 0x4b, 0xef, 0x3b, 0x70, 0xa0, 0xe4, 0x40, 0xff, 0xc3, 0xa9, 0xe6, 0x78, 0xf9, 
                    0x8b, 0x46, 0x80, 0x1e, 0x38, 0xe1, 0xb8, 0xa8, 0xe0, 0x0c, 0x23, 0x76, 0x1d, 0x25, 0x24, 0x05, 
                    0xf1, 0x6e, 0x94, 0x28, 0x9a, 0x84, 0xe8, 0xa3, 0x4f, 0x77, 0xd3, 0x85, 0xe2, 0x52, 0xf2, 0x82, 
                    0x50, 0x7a, 0x2f, 0x74, 0x53, 0xb3, 0x61, 0xaf, 0x39, 0x35, 0xde, 0xcd, 0x1f, 0x99, 0xac, 0xad, 
                    0x72, 0x2c, 0xdd, 0xd0, 0x87, 0xbe, 0x5e, 0xa6, 0xec, 0x04, 0xc6, 0x03, 0x34, 0xfb, 0xdb, 0x59, 
                    0xb6, 0xc2, 0x01, 0xf0, 0x5a, 0xed, 0xa7, 0x66, 0x21, 0x7f, 0x8a, 0x27, 0xc7, 0xc0, 0x29, 0xd7 
               },

               new byte[]
               {
                    0x93, 0xd9, 0x9a, 0xb5, 0x98, 0x22, 0x45, 0xfc, 0xba, 0x6a, 0xdf, 0x02, 0x9f, 0xdc, 0x51, 0x59, 
                    0x4a, 0x17, 0x2b, 0xc2, 0x94, 0xf4, 0xbb, 0xa3, 0x62, 0xe4, 0x71, 0xd4, 0xcd, 0x70, 0x16, 0xe1, 
                    0x49, 0x3c, 0xc0, 0xd8, 0x5c, 0x9b, 0xad, 0x85, 0x53, 0xa1, 0x7a, 0xc8, 0x2d, 0xe0, 0xd1, 0x72, 
                    0xa6, 0x2c, 0xc4, 0xe3, 0x76, 0x78, 0xb7, 0xb4, 0x09, 0x3b, 0x0e, 0x41, 0x4c, 0xde, 0xb2, 0x90, 
                    0x25, 0xa5, 0xd7, 0x03, 0x11, 0x00, 0xc3, 0x2e, 0x92, 0xef, 0x4e, 0x12, 0x9d, 0x7d, 0xcb, 0x35, 
                    0x10, 0xd5, 0x4f, 0x9e, 0x4d, 0xa9, 0x55, 0xc6, 0xd0, 0x7b, 0x18, 0x97, 0xd3, 0x36, 0xe6, 0x48, 
                    0x56, 0x81, 0x8f, 0x77, 0xcc, 0x9c, 0xb9, 0xe2, 0xac, 0xb8, 0x2f, 0x15, 0xa4, 0x7c, 0xda, 0x38, 
                    0x1e, 0x0b, 0x05, 0xd6, 0x14, 0x6e, 0x6c, 0x7e, 0x66, 0xfd, 0xb1, 0xe5, 0x60, 0xaf, 0x5e, 0x33, 
                    0x87, 0xc9, 0xf0, 0x5d, 0x6d, 0x3f, 0x88, 0x8d, 0xc7, 0xf7, 0x1d, 0xe9, 0xec, 0xed, 0x80, 0x29, 
                    0x27, 0xcf, 0x99, 0xa8, 0x50, 0x0f, 0x37, 0x24, 0x28, 0x30, 0x95, 0xd2, 0x3e, 0x5b, 0x40, 0x83, 
                    0xb3, 0x69, 0x57, 0x1f, 0x07, 0x1c, 0x8a, 0xbc, 0x20, 0xeb, 0xce, 0x8e, 0xab, 0xee, 0x31, 0xa2, 
                    0x73, 0xf9, 0xca, 0x3a, 0x1a, 0xfb, 0x0d, 0xc1, 0xfe, 0xfa, 0xf2, 0x6f, 0xbd, 0x96, 0xdd, 0x43, 
                    0x52, 0xb6, 0x08, 0xf3, 0xae, 0xbe, 0x19, 0x89, 0x32, 0x26, 0xb0, 0xea, 0x4b, 0x64, 0x84, 0x82, 
                    0x6b, 0xf5, 0x79, 0xbf, 0x01, 0x5f, 0x75, 0x63, 0x1b, 0x23, 0x3d, 0x68, 0x2a, 0x65, 0xe8, 0x91, 
                    0xf6, 0xff, 0x13, 0x58, 0xf1, 0x47, 0x0a, 0x7f, 0xc5, 0xa7, 0xe7, 0x61, 0x5a, 0x06, 0x46, 0x44, 
                    0x42, 0x04, 0xa0, 0xdb, 0x39, 0x86, 0x54, 0xaa, 0x8c, 0x34, 0x21, 0x8b, 0xf8, 0x0c, 0x74, 0x67 
               },

               new byte[]
               {
                    0x68, 0x8d, 0xca, 0x4d, 0x73, 0x4b, 0x4e, 0x2a, 0xd4, 0x52, 0x26, 0xb3, 0x54, 0x1e, 0x19, 0x1f, 
                    0x22, 0x03, 0x46, 0x3d, 0x2d, 0x4a, 0x53, 0x83, 0x13, 0x8a, 0xb7, 0xd5, 0x25, 0x79, 0xf5, 0xbd, 
                    0x58, 0x2f, 0x0d, 0x02, 0xed, 0x51, 0x9e, 0x11, 0xf2, 0x3e, 0x55, 0x5e, 0xd1, 0x16, 0x3c, 0x66, 
                    0x70, 0x5d, 0xf3, 0x45, 0x40, 0xcc, 0xe8, 0x94, 0x56, 0x08, 0xce, 0x1a, 0x3a, 0xd2, 0xe1, 0xdf, 
                    0xb5, 0x38, 0x6e, 0x0e, 0xe5, 0xf4, 0xf9, 0x86, 0xe9, 0x4f, 0xd6, 0x85, 0x23, 0xcf, 0x32, 0x99, 
                    0x31, 0x14, 0xae, 0xee, 0xc8, 0x48, 0xd3, 0x30, 0xa1, 0x92, 0x41, 0xb1, 0x18, 0xc4, 0x2c, 0x71, 
                    0x72, 0x44, 0x15, 0xfd, 0x37, 0xbe, 0x5f, 0xaa, 0x9b, 0x88, 0xd8, 0xab, 0x89, 0x9c, 0xfa, 0x60, 
                    0xea, 0xbc, 0x62, 0x0c, 0x24, 0xa6, 0xa8, 0xec, 0x67, 0x20, 0xdb, 0x7c, 0x28, 0xdd, 0xac, 0x5b, 
                    0x34, 0x7e, 0x10, 0xf1, 0x7b, 0x8f, 0x63, 0xa0, 0x05, 0x9a, 0x43, 0x77, 0x21, 0xbf, 0x27, 0x09, 
                    0xc3, 0x9f, 0xb6, 0xd7, 0x29, 0xc2, 0xeb, 0xc0, 0xa4, 0x8b, 0x8c, 0x1d, 0xfb, 0xff, 0xc1, 0xb2, 
                    0x97, 0x2e, 0xf8, 0x65, 0xf6, 0x75, 0x07, 0x04, 0x49, 0x33, 0xe4, 0xd9, 0xb9, 0xd0, 0x42, 0xc7, 
                    0x6c, 0x90, 0x00, 0x8e, 0x6f, 0x50, 0x01, 0xc5, 0xda, 0x47, 0x3f, 0xcd, 0x69, 0xa2, 0xe2, 0x7a, 
                    0xa7, 0xc6, 0x93, 0x0f, 0x0a, 0x06, 0xe6, 0x2b, 0x96, 0xa3, 0x1c, 0xaf, 0x6a, 0x12, 0x84, 0x39, 
                    0xe7, 0xb0, 0x82, 0xf7, 0xfe, 0x9d, 0x87, 0x5c, 0x81, 0x35, 0xde, 0xb4, 0xa5, 0xfc, 0x80, 0xef, 
                    0xcb, 0xbb, 0x6b, 0x76, 0xba, 0x5a, 0x7d, 0x78, 0x0b, 0x95, 0xe3, 0xad, 0x74, 0x98, 0x3b, 0x36, 
                    0x64, 0x6d, 0xdc, 0xf0, 0x59, 0xa9, 0x4c, 0x17, 0x7f, 0x91, 0xb8, 0xc9, 0x57, 0x1b, 0xe0, 0x61 
               }

          };


        private byte[][] sboxesForDecryption = 
          {
               new byte[]
               {
	               0xa4, 0xa2, 0xa9, 0xc5, 0x4e, 0xc9, 0x03, 0xd9, 0x7e, 0x0f, 0xd2, 0xad, 0xe7, 0xd3, 0x27, 0x5b, 
	               0xe3, 0xa1, 0xe8, 0xe6, 0x7c, 0x2a, 0x55, 0x0c, 0x86, 0x39, 0xd7, 0x8d, 0xb8, 0x12, 0x6f, 0x28, 
	               0xcd, 0x8a, 0x70, 0x56, 0x72, 0xf9, 0xbf, 0x4f, 0x73, 0xe9, 0xf7, 0x57, 0x16, 0xac, 0x50, 0xc0, 
	               0x9d, 0xb7, 0x47, 0x71, 0x60, 0xc4, 0x74, 0x43, 0x6c, 0x1f, 0x93, 0x77, 0xdc, 0xce, 0x20, 0x8c, 
	               0x99, 0x5f, 0x44, 0x01, 0xf5, 0x1e, 0x87, 0x5e, 0x61, 0x2c, 0x4b, 0x1d, 0x81, 0x15, 0xf4, 0x23, 
	               0xd6, 0xea, 0xe1, 0x67, 0xf1, 0x7f, 0xfe, 0xda, 0x3c, 0x07, 0x53, 0x6a, 0x84, 0x9c, 0xcb, 0x02, 
	               0x83, 0x33, 0xdd, 0x35, 0xe2, 0x59, 0x5a, 0x98, 0xa5, 0x92, 0x64, 0x04, 0x06, 0x10, 0x4d, 0x1c, 
	               0x97, 0x08, 0x31, 0xee, 0xab, 0x05, 0xaf, 0x79, 0xa0, 0x18, 0x46, 0x6d, 0xfc, 0x89, 0xd4, 0xc7, 
	               0xff, 0xf0, 0xcf, 0x42, 0x91, 0xf8, 0x68, 0x0a, 0x65, 0x8e, 0xb6, 0xfd, 0xc3, 0xef, 0x78, 0x4c, 
	               0xcc, 0x9e, 0x30, 0x2e, 0xbc, 0x0b, 0x54, 0x1a, 0xa6, 0xbb, 0x26, 0x80, 0x48, 0x94, 0x32, 0x7d, 
	               0xa7, 0x3f, 0xae, 0x22, 0x3d, 0x66, 0xaa, 0xf6, 0x00, 0x5d, 0xbd, 0x4a, 0xe0, 0x3b, 0xb4, 0x17, 
	               0x8b, 0x9f, 0x76, 0xb0, 0x24, 0x9a, 0x25, 0x63, 0xdb, 0xeb, 0x7a, 0x3e, 0x5c, 0xb3, 0xb1, 0x29, 
	               0xf2, 0xca, 0x58, 0x6e, 0xd8, 0xa8, 0x2f, 0x75, 0xdf, 0x14, 0xfb, 0x13, 0x49, 0x88, 0xb2, 0xec, 
	               0xe4, 0x34, 0x2d, 0x96, 0xc6, 0x3a, 0xed, 0x95, 0x0e, 0xe5, 0x85, 0x6b, 0x40, 0x21, 0x9b, 0x09, 
	               0x19, 0x2b, 0x52, 0xde, 0x45, 0xa3, 0xfa, 0x51, 0xc2, 0xb5, 0xd1, 0x90, 0xb9, 0xf3, 0x37, 0xc1, 
	               0x0d, 0xba, 0x41, 0x11, 0x38, 0x7b, 0xbe, 0xd0, 0xd5, 0x69, 0x36, 0xc8, 0x62, 0x1b, 0x82, 0x8f
               },

               new byte[]
               {
                    0x83, 0xf2, 0x2a, 0xeb, 0xe9, 0xbf, 0x7b, 0x9c, 0x34, 0x96, 0x8d, 0x98, 0xb9, 0x69, 0x8c, 0x29, 
                    0x3d, 0x88, 0x68, 0x06, 0x39, 0x11, 0x4c, 0x0e, 0xa0, 0x56, 0x40, 0x92, 0x15, 0xbc, 0xb3, 0xdc, 
                    0x6f, 0xf8, 0x26, 0xba, 0xbe, 0xbd, 0x31, 0xfb, 0xc3, 0xfe, 0x80, 0x61, 0xe1, 0x7a, 0x32, 0xd2, 
                    0x70, 0x20, 0xa1, 0x45, 0xec, 0xd9, 0x1a, 0x5d, 0xb4, 0xd8, 0x09, 0xa5, 0x55, 0x8e, 0x37, 0x76, 
                    0xa9, 0x67, 0x10, 0x17, 0x36, 0x65, 0xb1, 0x95, 0x62, 0x59, 0x74, 0xa3, 0x50, 0x2f, 0x4b, 0xc8, 
                    0xd0, 0x8f, 0xcd, 0xd4, 0x3c, 0x86, 0x12, 0x1d, 0x23, 0xef, 0xf4, 0x53, 0x19, 0x35, 0xe6, 0x7f, 
                    0x5e, 0xd6, 0x79, 0x51, 0x22, 0x14, 0xf7, 0x1e, 0x4a, 0x42, 0x9b, 0x41, 0x73, 0x2d, 0xc1, 0x5c, 
                    0xa6, 0xa2, 0xe0, 0x2e, 0xd3, 0x28, 0xbb, 0xc9, 0xae, 0x6a, 0xd1, 0x5a, 0x30, 0x90, 0x84, 0xf9, 
                    0xb2, 0x58, 0xcf, 0x7e, 0xc5, 0xcb, 0x97, 0xe4, 0x16, 0x6c, 0xfa, 0xb0, 0x6d, 0x1f, 0x52, 0x99, 
                    0x0d, 0x4e, 0x03, 0x91, 0xc2, 0x4d, 0x64, 0x77, 0x9f, 0xdd, 0xc4, 0x49, 0x8a, 0x9a, 0x24, 0x38, 
                    0xa7, 0x57, 0x85, 0xc7, 0x7c, 0x7d, 0xe7, 0xf6, 0xb7, 0xac, 0x27, 0x46, 0xde, 0xdf, 0x3b, 0xd7, 
                    0x9e, 0x2b, 0x0b, 0xd5, 0x13, 0x75, 0xf0, 0x72, 0xb6, 0x9d, 0x1b, 0x01, 0x3f, 0x44, 0xe5, 0x87, 
                    0xfd, 0x07, 0xf1, 0xab, 0x94, 0x18, 0xea, 0xfc, 0x3a, 0x82, 0x5f, 0x05, 0x54, 0xdb, 0x00, 0x8b, 
                    0xe3, 0x48, 0x0c, 0xca, 0x78, 0x89, 0x0a, 0xff, 0x3e, 0x5b, 0x81, 0xee, 0x71, 0xe2, 0xda, 0x2c, 
                    0xb8, 0xb5, 0xcc, 0x6e, 0xa8, 0x6b, 0xad, 0x60, 0xc6, 0x08, 0x04, 0x02, 0xe8, 0xf5, 0x4f, 0xa4, 
                    0xf3, 0xc0, 0xce, 0x43, 0x25, 0x1c, 0x21, 0x33, 0x0f, 0xaf, 0x47, 0xed, 0x66, 0x63, 0x93, 0xaa
               },

               new byte[]
               {
                    0x45, 0xd4, 0x0b, 0x43, 0xf1, 0x72, 0xed, 0xa4, 0xc2, 0x38, 0xe6, 0x71, 0xfd, 0xb6, 0x3a, 0x95, 
                    0x50, 0x44, 0x4b, 0xe2, 0x74, 0x6b, 0x1e, 0x11, 0x5a, 0xc6, 0xb4, 0xd8, 0xa5, 0x8a, 0x70, 0xa3, 
                    0xa8, 0xfa, 0x05, 0xd9, 0x97, 0x40, 0xc9, 0x90, 0x98, 0x8f, 0xdc, 0x12, 0x31, 0x2c, 0x47, 0x6a, 
                    0x99, 0xae, 0xc8, 0x7f, 0xf9, 0x4f, 0x5d, 0x96, 0x6f, 0xf4, 0xb3, 0x39, 0x21, 0xda, 0x9c, 0x85, 
                    0x9e, 0x3b, 0xf0, 0xbf, 0xef, 0x06, 0xee, 0xe5, 0x5f, 0x20, 0x10, 0xcc, 0x3c, 0x54, 0x4a, 0x52, 
                    0x94, 0x0e, 0xc0, 0x28, 0xf6, 0x56, 0x60, 0xa2, 0xe3, 0x0f, 0xec, 0x9d, 0x24, 0x83, 0x7e, 0xd5, 
                    0x7c, 0xeb, 0x18, 0xd7, 0xcd, 0xdd, 0x78, 0xff, 0xdb, 0xa1, 0x09, 0xd0, 0x76, 0x84, 0x75, 0xbb, 
                    0x1d, 0x1a, 0x2f, 0xb0, 0xfe, 0xd6, 0x34, 0x63, 0x35, 0xd2, 0x2a, 0x59, 0x6d, 0x4d, 0x77, 0xe7, 
                    0x8e, 0x61, 0xcf, 0x9f, 0xce, 0x27, 0xf5, 0x80, 0x86, 0xc7, 0xa6, 0xfb, 0xf8, 0x87, 0xab, 0x62, 
                    0x3f, 0xdf, 0x48, 0x00, 0x14, 0x9a, 0xbd, 0x5b, 0x04, 0x92, 0x02, 0x25, 0x65, 0x4c, 0x53, 0x0c, 
                    0xf2, 0x29, 0xaf, 0x17, 0x6c, 0x41, 0x30, 0xe9, 0x93, 0x55, 0xf7, 0xac, 0x68, 0x26, 0xc4, 0x7d, 
                    0xca, 0x7a, 0x3e, 0xa0, 0x37, 0x03, 0xc1, 0x36, 0x69, 0x66, 0x08, 0x16, 0xa7, 0xbc, 0xc5, 0xd3, 
                    0x22, 0xb7, 0x13, 0x46, 0x32, 0xe8, 0x57, 0x88, 0x2b, 0x81, 0xb2, 0x4e, 0x64, 0x1c, 0xaa, 0x91, 
                    0x58, 0x2e, 0x9b, 0x5c, 0x1b, 0x51, 0x73, 0x42, 0x23, 0x01, 0x6e, 0xf3, 0x0d, 0xbe, 0x3d, 0x0a, 
                    0x2d, 0x1f, 0x67, 0x33, 0x19, 0x7b, 0x5e, 0xea, 0xde, 0x8b, 0xcb, 0xa9, 0x8c, 0x8d, 0xad, 0x49, 
                    0x82, 0xe4, 0xba, 0xc3, 0x15, 0xd1, 0xe0, 0x89, 0xfc, 0xb1, 0xb9, 0xb5, 0x07, 0x79, 0xb8, 0xe1
               },

               new byte[]
               {
                    0xb2, 0xb6, 0x23, 0x11, 0xa7, 0x88, 0xc5, 0xa6, 0x39, 0x8f, 0xc4, 0xe8, 0x73, 0x22, 0x43, 0xc3, 
                    0x82, 0x27, 0xcd, 0x18, 0x51, 0x62, 0x2d, 0xf7, 0x5c, 0x0e, 0x3b, 0xfd, 0xca, 0x9b, 0x0d, 0x0f, 
                    0x79, 0x8c, 0x10, 0x4c, 0x74, 0x1c, 0x0a, 0x8e, 0x7c, 0x94, 0x07, 0xc7, 0x5e, 0x14, 0xa1, 0x21, 
                    0x57, 0x50, 0x4e, 0xa9, 0x80, 0xd9, 0xef, 0x64, 0x41, 0xcf, 0x3c, 0xee, 0x2e, 0x13, 0x29, 0xba, 
                    0x34, 0x5a, 0xae, 0x8a, 0x61, 0x33, 0x12, 0xb9, 0x55, 0xa8, 0x15, 0x05, 0xf6, 0x03, 0x06, 0x49, 
                    0xb5, 0x25, 0x09, 0x16, 0x0c, 0x2a, 0x38, 0xfc, 0x20, 0xf4, 0xe5, 0x7f, 0xd7, 0x31, 0x2b, 0x66, 
                    0x6f, 0xff, 0x72, 0x86, 0xf0, 0xa3, 0x2f, 0x78, 0x00, 0xbc, 0xcc, 0xe2, 0xb0, 0xf1, 0x42, 0xb4, 
                    0x30, 0x5f, 0x60, 0x04, 0xec, 0xa5, 0xe3, 0x8b, 0xe7, 0x1d, 0xbf, 0x84, 0x7b, 0xe6, 0x81, 0xf8, 
                    0xde, 0xd8, 0xd2, 0x17, 0xce, 0x4b, 0x47, 0xd6, 0x69, 0x6c, 0x19, 0x99, 0x9a, 0x01, 0xb3, 0x85, 
                    0xb1, 0xf9, 0x59, 0xc2, 0x37, 0xe9, 0xc8, 0xa0, 0xed, 0x4f, 0x89, 0x68, 0x6d, 0xd5, 0x26, 0x91, 
                    0x87, 0x58, 0xbd, 0xc9, 0x98, 0xdc, 0x75, 0xc0, 0x76, 0xf5, 0x67, 0x6b, 0x7e, 0xeb, 0x52, 0xcb, 
                    0xd1, 0x5b, 0x9f, 0x0b, 0xdb, 0x40, 0x92, 0x1a, 0xfa, 0xac, 0xe4, 0xe1, 0x71, 0x1f, 0x65, 0x8d, 
                    0x97, 0x9e, 0x95, 0x90, 0x5d, 0xb7, 0xc1, 0xaf, 0x54, 0xfb, 0x02, 0xe0, 0x35, 0xbb, 0x3a, 0x4d, 
                    0xad, 0x2c, 0x3d, 0x56, 0x08, 0x1b, 0x4a, 0x93, 0x6a, 0xab, 0xb8, 0x7a, 0xf2, 0x7d, 0xda, 0x3f, 
                    0xfe, 0x3e, 0xbe, 0xea, 0xaa, 0x44, 0xc6, 0xd0, 0x36, 0x48, 0x70, 0x96, 0x77, 0x24, 0x53, 0xdf, 
                    0xf3, 0x83, 0x28, 0x32, 0x45, 0x1e, 0xa4, 0xd3, 0xa2, 0x46, 0x6e, 0x9c, 0xdd, 0x63, 0xd4, 0x9d
               }
          };
        #endregion



        public virtual string AlgorithmName
        {
            get { return "Dstu7624"; }
        }

        public virtual int GetBlockSize()
        {
            return blockSizeBits / BITS_IN_BYTE;
        }

        public virtual bool IsPartialBlockOkay
        {
            get { return false; }
        }

        public virtual void Reset()
        {

        }

    }
}
