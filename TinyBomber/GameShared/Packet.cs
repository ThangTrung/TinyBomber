using System;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;

namespace GameShared
{
    public class Packet : IDisposable
    {
        private List<byte> buffer;
        private byte[] readableBuffer = null!;
        private int readPos;

        public Packet()
        {
            buffer = new List<byte>();
            readPos = 0;
        }

        public Packet(int _id)
        {
            buffer = new List<byte>();
            readPos = 0;
            Write(_id);
        }

        public Packet(byte[] _data)
        {
            buffer = new List<byte>();
            readPos = 0;
            SetBytes(_data);
        }

        public void SetBytes(byte[] _data)
        {
            Write(_data);
            readableBuffer = buffer.ToArray();
        }

        public void WriteLength()
        {
            buffer.InsertRange(0, BitConverter.GetBytes(buffer.Count));
        }

        public void InsertInt(int _value)
        {
            buffer.InsertRange(0, BitConverter.GetBytes(_value));
        }

        public byte[] ToArray()
        {
            readableBuffer = buffer.ToArray();
            return readableBuffer;
        }

        public int Length()
        {
            return buffer.Count;
        }

        public int UnreadLength()
        {
            return Length() - readPos;
        }

        public void Reset(bool _shouldReset = true)
        {
            if (_shouldReset)
            {
                buffer.Clear();
                readableBuffer = null!;
                readPos = 0;
            }
            else
            {
                readPos -= 4;
            }
        }

        public void Write(byte _value) { buffer.Add(_value); }
        public void Write(byte[] _value) { buffer.AddRange(_value); }
        public void Write(short _value) { buffer.AddRange(BitConverter.GetBytes(_value)); }
        public void Write(int _value) { buffer.AddRange(BitConverter.GetBytes(_value)); }
        public void Write(long _value) { buffer.AddRange(BitConverter.GetBytes(_value)); }
        public void Write(float _value) { buffer.AddRange(BitConverter.GetBytes(_value)); }
        public void Write(bool _value) { buffer.AddRange(BitConverter.GetBytes(_value)); }
        public void Write(string _value)
        {
            Write(_value.Length);
            buffer.AddRange(Encoding.UTF8.GetBytes(_value));
        }

        public byte ReadByte(bool _moveReadPos = true)
        {
            if (buffer.Count > readPos)
            {
                byte _value = readableBuffer[readPos];
                if (_moveReadPos) { readPos += 1; }
                return _value;
            }
            throw new Exception("Could not read value of type 'byte'!");
        }

        public byte[] ReadBytes(int _length, bool _moveReadPos = true)
        {
            if (buffer.Count > readPos)
            {
                byte[] _value = buffer.GetRange(readPos, _length).ToArray();
                if (_moveReadPos) { readPos += _length; }
                return _value;
            }
            throw new Exception("Could not read value of type 'byte[]'!");
        }

        public int ReadInt(bool _moveReadPos = true)
        {
            if (buffer.Count > readPos)
            {
                int _value = BitConverter.ToInt32(readableBuffer, readPos);
                if (_moveReadPos) { readPos += 4; }
                return _value;
            }
            throw new Exception("Could not read value of type 'int'!");
        }

        public float ReadFloat(bool _moveReadPos = true)
        {
            if (buffer.Count > readPos)
            {
                float _value = BitConverter.ToSingle(readableBuffer, readPos);
                if (_moveReadPos) { readPos += 4; }
                return _value;
            }
            throw new Exception("Could not read value of type 'float'!");
        }

        public bool ReadBool(bool _moveReadPos = true)
        {
            if (buffer.Count > readPos)
            {
                bool _value = BitConverter.ToBoolean(readableBuffer, readPos);
                if (_moveReadPos) { readPos += 1; }
                return _value;
            }
            throw new Exception("Could not read value of type 'bool'!");
        }

        public string ReadString(bool _moveReadPos = true)
        {
            try
            {
                int _length = ReadInt(); 
                string _value = Encoding.UTF8.GetString(readableBuffer, readPos, _length); 
                if (_moveReadPos) { readPos += _length; }
                return _value;
            }
            catch
            {
                throw new Exception("Could not read value of type 'string'!");
            }
        }

        public void EncryptPayload()
        {
            try
            {
                byte[] lengthBytes = buffer.GetRange(0, 4).ToArray();
                int originalLength = BitConverter.ToInt32(lengthBytes, 0);

                byte[] dataToEncrypt = buffer.GetRange(4, buffer.Count - 4).ToArray();

                byte[] iv = new byte[Constants.AES_IV_SIZE];
                using (var rng = new RNGCryptoServiceProvider())
                {
                    rng.GetBytes(iv);
                }

                byte[] encryptedData = EncryptAES(dataToEncrypt, iv);

                byte[] hmac = CalculateHMAC(iv, encryptedData);

                buffer.Clear();

                buffer.AddRange(lengthBytes);

                buffer.AddRange(iv);

                buffer.AddRange(encryptedData);

                buffer.AddRange(hmac);

                int newLength = Constants.AES_IV_SIZE + encryptedData.Length + Constants.HMAC_SIZE;
                byte[] newLengthBytes = BitConverter.GetBytes(newLength);

                for (int i = 0; i < 4; i++)
                {
                    buffer[i] = newLengthBytes[i];
                }

                readableBuffer = buffer.ToArray();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error encrypting packet: {ex.Message}", ex);
            }
        }

        public void DecryptPayload()
        {
            try
            {
                if (buffer.Count < 4 + Constants.AES_IV_SIZE + Constants.HMAC_SIZE)
                {
                    throw new Exception($"Packet too small for decryption: {buffer.Count} bytes");
                }

                byte[] lengthBytes = buffer.GetRange(0, 4).ToArray();
                int packetLength = BitConverter.ToInt32(lengthBytes, 0);

                byte[] iv = buffer.GetRange(4, Constants.AES_IV_SIZE).ToArray();

                int encryptedDataStart = 4 + Constants.AES_IV_SIZE;
                int encryptedDataLength = packetLength - Constants.AES_IV_SIZE - Constants.HMAC_SIZE;

                if (encryptedDataLength <= 0)
                {
                    throw new Exception($"Invalid encrypted data length: {encryptedDataLength}");
                }

                byte[] encryptedData = buffer.GetRange(encryptedDataStart, encryptedDataLength).ToArray();

                int hmacStart = 4 + Constants.AES_IV_SIZE + encryptedDataLength;
                byte[] receivedHmac = buffer.GetRange(hmacStart, Constants.HMAC_SIZE).ToArray();

                byte[] calculatedHmac = CalculateHMAC(iv, encryptedData);

                if (!ByteArrayEquals(calculatedHmac, receivedHmac))
                {
                    throw new Exception("HMAC verification failed - packet may have been tampered with!");
                }

                byte[] decryptedData = DecryptAES(encryptedData, iv);

                buffer.Clear();

                buffer.AddRange(lengthBytes);

                buffer.AddRange(decryptedData);

                readableBuffer = buffer.ToArray();
                readPos = 0;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error decrypting packet: {ex.Message}", ex);
            }
        }

        private byte[] EncryptAES(byte[] plaintext, byte[] iv)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Constants.ENCRYPTION_KEY;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                {
                    return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
                }
            }
        }

        private byte[] DecryptAES(byte[] ciphertext, byte[] iv)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Constants.ENCRYPTION_KEY;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                {
                    return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                }
            }
        }

        private byte[] CalculateHMAC(byte[] iv, byte[] encryptedData)
        {
            // Combine IV + EncryptedData for HMAC input
            byte[] hmacInput = new byte[iv.Length + encryptedData.Length];
            Array.Copy(iv, 0, hmacInput, 0, iv.Length);
            Array.Copy(encryptedData, 0, hmacInput, iv.Length, encryptedData.Length);

            using (var hmac = new HMACSHA256(Constants.ENCRYPTION_KEY))
            {
                return hmac.ComputeHash(hmacInput);
            }
        }

        private bool ByteArrayEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            int result = 0;
            for (int i = 0; i < a.Length; i++)
            {
                result |= a[i] ^ b[i];
            }

            return result == 0;
        }

        private bool disposed = false;
        protected virtual void Dispose(bool _disposing)
        {
            if (!disposed)
            {
                if (_disposing)
                {
                    buffer = null!;
                    readableBuffer = null!;
                    readPos = 0;
                }
                disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
