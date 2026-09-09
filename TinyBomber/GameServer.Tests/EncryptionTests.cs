using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;
using GameShared;

namespace GameServer.Tests
{
    /// Test Coverage:
    /// 1. Encrypt/Decrypt roundtrip
    /// 2. Tampering detection
    /// 3. Performance metrics
    /// 4. Random IV generation
    /// 5. Packet structure integrity
    /// 6. Error handling
    /// 7. Different payload types
    /// 8. Concurrent encryption
    /// 
    public class EncryptionTests
    {
        // ===== ROUNDTRIP TESTS =====

        [Fact]
        public void Encrypt_Decrypt_RoundTrip_SimpleMessage()
        {
            // Arrange - Create packet with simple message
            using (Packet sendPacket = new Packet((int)ServerPackets.welcome))
            {
                string testMessage = "Hello, Tiny Bomber!";
                int testInt = 42;
                sendPacket.Write(testMessage);
                sendPacket.Write(testInt);

                // Get original data
                byte[] originalData = sendPacket.ToArray();

                // Simulate send: WriteLength + Encrypt
                sendPacket.WriteLength();
                sendPacket.EncryptPayload();
                byte[] encryptedData = sendPacket.ToArray();

                // Act - Simulate receive: Create new packet, decrypt
                using (Packet receivePacket = new Packet(encryptedData))
                {
                    receivePacket.DecryptPayload();

                    // Read back data
                    int packetId = receivePacket.ReadInt();
                    string receivedMessage = receivePacket.ReadString();
                    int receivedInt = receivePacket.ReadInt();

                    // Assert
                    Assert.Equal((int)ServerPackets.welcome, packetId);
                    Assert.Equal(testMessage, receivedMessage);
                    Assert.Equal(testInt, receivedInt);
                }
            }
        }

        [Fact]
        public void Encrypt_Decrypt_RoundTrip_MultipleTypes()
        {
            // Arrange - Various data types
            using (Packet sendPacket = new Packet((int)ServerPackets.playerPosition))
            {
                float posX = 123.45f;
                float posY = 456.78f;
                bool isMoving = true;
                string playerName = "TestPlayer";

                sendPacket.Write(posX);
                sendPacket.Write(posY);
                sendPacket.Write(isMoving);
                sendPacket.Write(playerName);

                sendPacket.WriteLength();
                sendPacket.EncryptPayload();
                byte[] encryptedData = sendPacket.ToArray();

                // Act & Assert
                using (Packet receivePacket = new Packet(encryptedData))
                {
                    receivePacket.DecryptPayload();

                    int packetId = receivePacket.ReadInt();
                    float rxPosX = receivePacket.ReadFloat();
                    float rxPosY = receivePacket.ReadFloat();
                    bool rxIsMoving = receivePacket.ReadBool();
                    string rxPlayerName = receivePacket.ReadString();

                    Assert.Equal((int)ServerPackets.playerPosition, packetId);
                    Assert.Equal(posX, rxPosX);
                    Assert.Equal(posY, rxPosY);
                    Assert.Equal(isMoving, rxIsMoving);
                    Assert.Equal(playerName, rxPlayerName);
                }
            }
        }

        [Fact]
        public void Encrypt_Decrypt_RoundTrip_EmptyPayload()
        {
            // Arrange - Packet with only ID, no payload
            using (Packet sendPacket = new Packet((int)ServerPackets.spawnPlayer))
            {
                sendPacket.WriteLength();
                sendPacket.EncryptPayload();
                byte[] encryptedData = sendPacket.ToArray();

                // Act
                using (Packet receivePacket = new Packet(encryptedData))
                {
                    receivePacket.DecryptPayload();
                    int packetId = receivePacket.ReadInt();

                    // Assert
                    Assert.Equal((int)ServerPackets.spawnPlayer, packetId);
                }
            }
        }

        // ===== TAMPERING DETECTION TESTS =====

        [Fact]
        public void Decrypt_TamperedEncryptedData_ThrowsException()
        {
            byte[] encryptedData;
            // Arrange
            using (Packet sendPacket = new Packet((int)ServerPackets.welcome))
            {
                sendPacket.Write("Secret message");
                sendPacket.WriteLength();
                sendPacket.EncryptPayload();
                encryptedData = sendPacket.ToArray();
            }

            // Act - Tamper: Flip a bit in encrypted data (middle section)
            int tamperPosition = 30; // Middle of encrypted section
            encryptedData[tamperPosition] ^= 0xFF; // Flip all bits

            // Assert - Should detect tampering
            using (Packet receivePacket = new Packet(encryptedData))
            {
                Exception ex = Assert.Throws<Exception>(() => receivePacket.DecryptPayload());
                Assert.Contains("HMAC verification failed", ex.Message);
            }
        }

        [Fact]
        public void Decrypt_TamperedHMAC_ThrowsException()
        {
            byte[] encryptedData;
            // Arrange
            using (Packet sendPacket = new Packet((int)ServerPackets.welcome))
            {
                sendPacket.Write("Secret message");
                sendPacket.WriteLength();
                sendPacket.EncryptPayload();
                encryptedData = sendPacket.ToArray();
            }

            // Act - Tamper with HMAC (last 32 bytes)
            int hmacStart = encryptedData.Length - Constants.HMAC_SIZE;
            encryptedData[hmacStart] ^= 0x01; // Flip 1 bit in HMAC

            // Assert
            using (Packet receivePacket = new Packet(encryptedData))
            {
                Exception ex = Assert.Throws<Exception>(() => receivePacket.DecryptPayload());
                Assert.Contains("HMAC verification failed", ex.Message);
            }
        }

        [Fact]
        public void Decrypt_TamperedLength_ThrowsException()
        {
            byte[] encryptedData;
            // Arrange
            using (Packet sendPacket = new Packet((int)ServerPackets.welcome))
            {
                sendPacket.Write("Test");
                sendPacket.WriteLength();
                sendPacket.EncryptPayload();
                encryptedData = sendPacket.ToArray();
            }

            // Act - Tamper with length (first 4 bytes) - won't help because HMAC covers different data
            // But packet structure will be wrong
            encryptedData[0] ^= 0xFF;

            // Assert - Should detect as invalid structure or HMAC fail
            using (Packet receivePacket = new Packet(encryptedData))
            {
                // This should throw due to invalid packet length or HMAC verification
                Assert.Throws<Exception>(() => receivePacket.DecryptPayload());
            }
        }

        // ===== RANDOM IV TESTS =====

        [Fact]
        public void Encrypt_DifferentIVsForSamePayload()
        {
            // Arrange - Create two packets with identical payload
            byte[] encrypted1, encrypted2;

            using (Packet packet1 = new Packet((int)ServerPackets.welcome))
            {
                packet1.Write("Same message");
                packet1.WriteLength();
                packet1.EncryptPayload();
                encrypted1 = packet1.ToArray();
            }

            using (Packet packet2 = new Packet((int)ServerPackets.welcome))
            {
                packet2.Write("Same message");
                packet2.WriteLength();
                packet2.EncryptPayload();
                encrypted2 = packet2.ToArray();
            }

            // Act & Assert - Ciphertexts should be different (different IVs)
            Assert.NotEqual(encrypted1, encrypted2);

            // But both should decrypt to same plaintext
            using (Packet rx1 = new Packet(encrypted1))
            {
                rx1.DecryptPayload();
                string msg1 = rx1.ReadString();

                using (Packet rx2 = new Packet(encrypted2))
                {
                    rx2.DecryptPayload();
                    string msg2 = rx2.ReadString();

                    Assert.Equal(msg1, msg2);
                }
            }
        }

        // ===== PERFORMANCE TESTS =====

        [Fact]
        public void Encrypt_Performance_LessThan2Milliseconds()
        {
            // Arrange
            using (Packet packet = new Packet((int)ServerPackets.playerPosition))
            {
                for (int i = 0; i < 10; i++)
                {
                    packet.Write(123.45f);
                    packet.Write(456.78f);
                    packet.Write("PlayerData");
                }
                packet.WriteLength();

                // Act
                var stopwatch = Stopwatch.StartNew();
                packet.EncryptPayload();
                stopwatch.Stop();

                // Assert - Should be fast
                Assert.True(stopwatch.ElapsedMilliseconds < 2,
                    $"Encryption took {stopwatch.ElapsedMilliseconds}ms, expected < 2ms");
            }
        }

        [Fact]
        public void Decrypt_Performance_LessThan2Milliseconds()
        {
            // Arrange
            byte[] encryptedData;
            using (Packet packet = new Packet((int)ServerPackets.playerPosition))
            {
                for (int i = 0; i < 10; i++)
                {
                    packet.Write(123.45f);
                    packet.Write(456.78f);
                    packet.Write("PlayerData");
                }
                packet.WriteLength();
                packet.EncryptPayload();
                encryptedData = packet.ToArray();
            }

            // Act
            using (Packet rxPacket = new Packet(encryptedData))
            {
                var stopwatch = Stopwatch.StartNew();
                rxPacket.DecryptPayload();
                stopwatch.Stop();

                // Assert
                Assert.True(stopwatch.ElapsedMilliseconds < 2,
                    $"Decryption took {stopwatch.ElapsedMilliseconds}ms, expected < 2ms");
            }
        }

        // ===== PACKET STRUCTURE TESTS =====

        [Fact]
        public void Encrypt_LengthHeaderNotEncrypted()
        {
            // Arrange
            using (Packet packet = new Packet((int)ServerPackets.welcome))
            {
                packet.Write("Test");
                packet.WriteLength();
                int lengthBefore = packet.Length();

                packet.EncryptPayload();
                byte[] encrypted = packet.ToArray();

                // Assert - First 4 bytes should be readable as length
                // (they represent Length of (IV + EncryptedData + HMAC))
                int readableLength = BitConverter.ToInt32(encrypted, 0);

                // Length should be approximately: IV (16) + EncryptedData + HMAC (32)
                // At minimum: 16 + 4 (PacketID) + 32 = 52 (with minimal data)
                Assert.True(readableLength >= 16 + Constants.HMAC_SIZE,
                    $"Invalid length header: {readableLength}");
            }
        }

        [Fact]
        public void Encrypt_IVIsNotEncrypted()
        {
            // Arrange
            byte[] iv1, iv2;
            using (Packet packet1 = new Packet((int)ServerPackets.welcome))
            {
                packet1.Write("Same data");
                packet1.WriteLength();
                packet1.EncryptPayload();
                byte[] encrypted1 = packet1.ToArray();

                // Extract IV (bytes 4-19)
                iv1 = new byte[Constants.AES_IV_SIZE];
                Array.Copy(encrypted1, 4, iv1, 0, Constants.AES_IV_SIZE);
            }

            using (Packet packet2 = new Packet((int)ServerPackets.welcome))
            {
                packet2.Write("Same data");
                packet2.WriteLength();
                packet2.EncryptPayload();
                byte[] encrypted2 = packet2.ToArray();

                // Extract IV (bytes 4-19)
                iv2 = new byte[Constants.AES_IV_SIZE];
                Array.Copy(encrypted2, 4, iv2, 0, Constants.AES_IV_SIZE);
            }

            // Assert - IVs should be different (random)
            Assert.NotEqual(iv1, iv2);
        }

        // ===== ERROR HANDLING TESTS =====

        [Fact]
        public void Decrypt_PacketTooSmall_ThrowsException()
        {
            // Arrange - Create packet smaller than minimum (52 bytes)
            byte[] tinyData = new byte[20];

            // Act & Assert
            using (Packet packet = new Packet(tinyData))
            {
                Exception ex = Assert.Throws<Exception>(() => packet.DecryptPayload());
                Assert.Contains("too small", ex.Message);
            }
        }

        [Fact]
        public void Decrypt_InvalidData_ThrowsException()
        {
            // Arrange - Random garbage
            byte[] randomData = new byte[100];
            var rand = new Random(42);
            rand.NextBytes(randomData);

            // Act & Assert
            using (Packet packet = new Packet(randomData))
            {
                // Should throw either due to HMAC fail or format error
                Assert.Throws<Exception>(() => packet.DecryptPayload());
            }
        }

        // ===== STRESS TEST =====

        [Fact]
        public void Encrypt_Decrypt_StressTest_1000Packets()
        {
            // Arrange
            int successCount = 0;

            // Act
            for (int i = 0; i < 1000; i++)
            {
                try
                {
                    using (Packet sendPacket = new Packet((int)ServerPackets.playerPosition))
                    {
                        sendPacket.Write((float)i);
                        sendPacket.Write((float)(i * 2));
                        sendPacket.Write($"Player{i}");
                        sendPacket.WriteLength();
                        sendPacket.EncryptPayload();
                        byte[] encrypted = sendPacket.ToArray();

                        using (Packet rxPacket = new Packet(encrypted))
                        {
                            rxPacket.DecryptPayload();
                            int id = rxPacket.ReadInt();
                            float x = rxPacket.ReadFloat();
                            float y = rxPacket.ReadFloat();
                            string name = rxPacket.ReadString();

                            if (x == (float)i && y == (float)(i * 2) && name == $"Player{i}")
                            {
                                successCount++;
                            }
                        }
                    }
                }
                catch
                {
                    // Count as failure
                }
            }

            // Assert
            Assert.Equal(1000, successCount);
        }
    }
}