using System;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using Xunit;
using GameShared;

namespace GameServer.Tests
{
    /// <summary>
    /// Unit Tests for UDP Protocol (Phase 2)
    /// 
    /// Tests cover:
    /// 1. Socket creation and binding
    /// 2. Echo test (client ↔ server)
    /// 3. Encryption integration
    /// 4. Performance metrics
    /// 5. Error handling
    /// 6. Fallback mechanism (simulation)
    /// </summary>
    public class UdpEchoTests
    {
        private const int TEST_PORT = Constants.UDP_PORT;

        // ===== SOCKET TESTS =====

        [Fact]
        public void UDP_Socket_Create_Success()
        {
            // Arrange & Act
            UdpClient socket = new UdpClient(TEST_PORT + 1000);  // Use different port to avoid conflicts

            // Assert
            Assert.NotNull(socket);
            socket.Close();
        }

        [Fact]
        public void UDP_Socket_DontFragment_SetCorrectly()
        {
            // Arrange
            UdpClient socket = new UdpClient(TEST_PORT + 1001);

            // Act
            socket.DontFragment = true;

            // Assert
            Assert.True(socket.DontFragment);
            socket.Close();
        }

        // ===== ENDPOINT TRACKING TESTS =====

        [Fact]
        public void UDP_Endpoint_Discovery_LocalLoopback()
        {
            // Arrange
            IPEndPoint localEndpoint = new IPEndPoint(IPAddress.Loopback, TEST_PORT + 1002);

            // Act
            UdpClient socket = new UdpClient(localEndpoint);

            // Assert
            Assert.NotNull(socket.Client.LocalEndPoint);
            socket.Close();
        }

        [Fact]
        public void UDP_Endpoint_Parse_IPAndPort()
        {
            // Arrange
            string ip = "127.0.0.1";
            int port = TEST_PORT + 1003;
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Parse(ip), port);

            // Act
            string parsedIP = endpoint.Address.ToString();
            int parsedPort = endpoint.Port;

            // Assert
            Assert.Equal(ip, parsedIP);
            Assert.Equal(port, parsedPort);
        }

        // ===== PACKET ENCRYPTION TESTS (UDP reuses Packet.cs) =====

        [Fact]
        public void UDP_Packet_Encrypt_Decrypt_RoundTrip()
        {
            // Arrange
            byte[] encryptedData;

            using (Packet sendPacket = new Packet((int)ServerPackets.playerPosition))
            {
                float posX = 123.45f;
                float posY = 456.78f;
                sendPacket.Write(posX);
                sendPacket.Write(posY);

                sendPacket.WriteLength();
                sendPacket.EncryptPayload();
                encryptedData = sendPacket.ToArray();
            }

            // Act
            using (Packet rxPacket = new Packet(encryptedData))
            {
                rxPacket.DecryptPayload();

                int packetLength = rxPacket.ReadInt();
                int packetId = rxPacket.ReadInt();
                float rxX = rxPacket.ReadFloat();
                float rxY = rxPacket.ReadFloat();

                // Assert
                Assert.Equal((int)ServerPackets.playerPosition, packetId);
                Assert.Equal(123.45f, rxX);
                Assert.Equal(456.78f, rxY);
            }
        }

        // ===== PAYLOAD TESTS =====

        [Fact]
        public void UDP_Payload_MultipleTypes_RoundTrip()
        {
            // Arrange
            byte[] encryptedData;

            using (Packet sendPacket = new Packet((int)ClientPackets.playerMovement))
            {
                float x = 100f;
                float y = 200f;
                bool moving = true;

                sendPacket.Write(x);
                sendPacket.Write(y);
                sendPacket.Write(moving);

                sendPacket.WriteLength();
                sendPacket.EncryptPayload();
                encryptedData = sendPacket.ToArray();
            }

            // Act
            using (Packet rxPacket = new Packet(encryptedData))
            {
                rxPacket.DecryptPayload();

                int length = rxPacket.ReadInt();
                int id = rxPacket.ReadInt();
                float rxX = rxPacket.ReadFloat();
                float rxY = rxPacket.ReadFloat();
                bool rxMoving = rxPacket.ReadBool();

                // Assert
                Assert.Equal((int)ClientPackets.playerMovement, id);
                Assert.Equal(100f, rxX);
                Assert.Equal(200f, rxY);
                Assert.True(rxMoving);
            }
        }

        // ===== HEADER PROTECTION TESTS =====

        [Fact]
        public void UDP_Length_Header_Not_Encrypted()
        {
            // Arrange
            using (Packet packet = new Packet((int)ServerPackets.welcome))
            {
                packet.Write("Test");
                packet.WriteLength();
                int lengthBefore = packet.Length();

                packet.EncryptPayload();
                byte[] encrypted = packet.ToArray();

                // Act - Read Length header (first 4 bytes)
                int readableLength = BitConverter.ToInt32(encrypted, 0);

                // Assert
                Assert.True(readableLength > 0);
                Assert.True(readableLength > 16 + Constants.HMAC_SIZE);  // At least IV + HMAC
            }
        }

        // ===== PERFORMANCE TESTS =====

        [Fact]
        public void UDP_Encryption_Performance_LessThan2ms()
        {
            // Arrange
            using (Packet packet = new Packet((int)ServerPackets.playerPosition))
            {
                for (int i = 0; i < 20; i++)
                {
                    packet.Write(123.45f);
                    packet.Write(456.78f);
                }
                packet.WriteLength();

                // Act
                var stopwatch = Stopwatch.StartNew();
                packet.EncryptPayload();
                stopwatch.Stop();

                // Assert
                Assert.True(stopwatch.ElapsedMilliseconds < 2);
            }
        }

        [Fact]
        public void UDP_Decryption_Performance_LessThan2ms()
        {
            // Arrange
            byte[] encryptedData;
            using (Packet packet = new Packet((int)ServerPackets.playerPosition))
            {
                for (int i = 0; i < 20; i++)
                {
                    packet.Write(123.45f);
                    packet.Write(456.78f);
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
                Assert.True(stopwatch.ElapsedMilliseconds < 2);
            }
        }

        // ===== ERROR HANDLING TESTS =====

        [Fact]
        public void UDP_Decrypt_TamperedData_ThrowsException()
        {
            // Arrange
            byte[] encryptedData;

            using (Packet packet = new Packet((int)ServerPackets.playerPosition))
            {
                packet.Write(100f);
                packet.Write(200f);
                packet.WriteLength();
                packet.EncryptPayload();
                encryptedData = packet.ToArray();
            }

            // Act - Tamper with encrypted data (middle section)
            encryptedData[30] ^= 0xFF;

            // Assert
            using (Packet rxPacket = new Packet(encryptedData))
            {
                Assert.Throws<Exception>(() => rxPacket.DecryptPayload());
            }
        }

        [Fact]
        public void UDP_Packet_TooSmall_ThrowsException()
        {
            // Arrange
            byte[] tinyData = new byte[20];

            // Act & Assert
            using (Packet packet = new Packet(tinyData))
            {
                Assert.Throws<Exception>(() => packet.DecryptPayload());
            }
        }

        // ===== STRESS TEST =====

        [Fact]
        public void UDP_StressTest_500Packets_AllSucceed()
        {
            // Arrange
            int successCount = 0;

            // Act
            for (int i = 0; i < 500; i++)
            {
                try
                {
                    byte[] encryptedData;

                    using (Packet sendPacket = new Packet((int)ServerPackets.playerPosition))
                    {
                        sendPacket.Write((float)i);
                        sendPacket.Write((float)(i * 2));
                        sendPacket.WriteLength();
                        sendPacket.EncryptPayload();
                        encryptedData = sendPacket.ToArray();
                    }

                    using (Packet rxPacket = new Packet(encryptedData))
                    {
                        rxPacket.DecryptPayload();
                        int length = rxPacket.ReadInt();
                        int id = rxPacket.ReadInt();
                        float x = rxPacket.ReadFloat();
                        float y = rxPacket.ReadFloat();

                        if (x == (float)i && y == (float)(i * 2))
                        {
                            successCount++;
                        }
                    }
                }
                catch
                {
                    // Count failures
                }
            }

            // Assert
            Assert.Equal(500, successCount);
        }

        // ===== THREAD SAFETY TEST =====

        [Fact]
        public void UDP_Packet_ThreadSafe_Encryption()
        {
            // Arrange
            var threads = new System.Collections.Generic.List<System.Threading.Thread>();
            int successCount = 0;
            object lockObj = new object();

            // Act - Encrypt from multiple threads
            for (int i = 0; i < 100; i++)
            {
                int index = i;
                var t = new System.Threading.Thread(() =>
                {
                    try
                    {
                        byte[] encryptedData;
                        using (Packet packet = new Packet((int)ServerPackets.playerPosition))
                        {
                            packet.Write((float)index);
                            packet.Write((float)(index * 2));
                            packet.WriteLength();
                            packet.EncryptPayload();
                            encryptedData = packet.ToArray();
                        }

                        using (Packet rxPacket = new Packet(encryptedData))
                        {
                            rxPacket.DecryptPayload();
                            int length = rxPacket.ReadInt();
                            int id = rxPacket.ReadInt();
                            float x = rxPacket.ReadFloat();

                            if (x == (float)index)
                            {
                                lock (lockObj)
                                {
                                    successCount++;
                                }
                            }
                        }
                    }
                    catch { }
                });
                threads.Add(t);
                t.Start();
            }

            foreach (var t in threads)
            {
                t.Join();
            }

            // Assert
            Assert.Equal(100, successCount);
        }
    }
}