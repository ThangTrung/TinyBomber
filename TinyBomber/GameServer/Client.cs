using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameShared;

namespace GameServer
{
    class Client
    {
        public int id;
        public Player? player;
        internal GameLogic? Room { get; set; }
        internal string? Username { get; set; }
        internal bool UdpConfirmed { get; set; }
        internal bool IsReady => Username != null && UdpConfirmed;
        public TCP tcp;
        public UDP udp;

        public Client(int _clientId)
        {
            id = _clientId;
            tcp = new TCP(id);
            udp = new UDP(id);
        }

        public class TCP
        {
            private const int HandshakeTimeoutSeconds = 20;
            public TcpClient socket = null!;
            private readonly int id;
            private long? handshakeDeadline;
            private NetworkStream stream = null!;
            private byte[] receiveBuffer = null!;
            private TcpFrameReader frameReader = null!;
            private readonly Queue<byte[]> pendingWrites = new();
            private bool writing;
            private volatile bool closed;
            // Read and written by the server update thread.
            internal byte[]? SessionToken { get; set; }

            public TCP(int _id)
            {
                id = _id;
            }

            internal void StartHandshake(long now)
            {
                handshakeDeadline ??= now + HandshakeTimeoutSeconds * Stopwatch.Frequency;
            }

            internal bool HandshakeHasExpired(long now) =>
                handshakeDeadline is long deadline && now >= deadline;

            public void Connect(TcpClient _socket)
            {
                socket = _socket;
                StartHandshake(Stopwatch.GetTimestamp());
                socket.ReceiveBufferSize = Constants.DATA_BUFFER_SIZE;
                socket.SendBufferSize = Constants.DATA_BUFFER_SIZE;

                stream = socket.GetStream();
                receiveBuffer = new byte[Constants.DATA_BUFFER_SIZE];
                frameReader = new TcpFrameReader();

                stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);

                ServerSend.ServerHello(this, id);
            }

            internal void Close()
            {
                closed = true;
                socket?.Close();
            }

            public void SendData(Packet packet)
            {
                if (socket == null) return;

                lock (pendingWrites)
                {
                    pendingWrites.Enqueue(packet.ToArray());
                    if (writing) return;
                    writing = true;
                }

                _ = WritePending();
            }

            private async Task WritePending()
            {
                try
                {
                    while (true)
                    {
                        byte[] frame;
                        lock (pendingWrites)
                        {
                            if (!pendingWrites.TryDequeue(out frame!))
                            {
                                writing = false;
                                return;
                            }
                        }

                        try
                        {
                            await stream.WriteAsync(frame.AsMemory()).ConfigureAwait(false);
                        }
                        catch
                        {
                            Server.Network.RecordTcpSendError();
                            throw;
                        }
                        Server.Network.RecordTcpWrite(frame.Length);
                    }
                }
                catch (Exception ex)
                {
                    if (closed) return;
                    Console.WriteLine($"Loi khi gui du lieu toi Player {id}: {ex.Message}");
                    ThreadManager.EnqueueAction(() => Server.Disconnect(id, this));
                }
            }

            private void HandleFrame(byte[] _data)
            {
                using (Packet _packet = new Packet(_data))
                {
                    _packet.DecryptPayload();

                    _packet.ReadInt();
                    int packetId = _packet.ReadInt(false);

                    if (packetId == (int)ClientPackets.clientHello)
                    {
                        var hello = ProtocolV2.ReadClientHello(_packet);
                        EnqueueCurrent(slot => ServerHandle.ClientHello(slot, this, hello.Version, hello.SlotId, hello.Username));
                        return;
                    }
                    if (packetId == (int)ClientPackets.udpConfirm)
                    {
                        var confirm = ProtocolV2.ReadUdpConfirm(_packet);
                        EnqueueCurrent(slot => ServerHandle.ConfirmUdp(slot, this, confirm.Token, confirm.Nonce));
                        return;
                    }
                    if (packetId is (int)ClientPackets.welcomeReceived or (int)ClientPackets.udpReady)
                        throw new InvalidDataException("Legacy v1 handshake packet on a v2 connection.");

                    _packet.ReadInt();
                    if (packetId == (int)ClientPackets.placeBomb)
                    {
                        int roomId = _packet.ReadInt();
                        int roundId = _packet.ReadInt();
                        EnqueueCurrent(slot => slot.Room?.PlaceBomb(slot, roomId, roundId));
                    }
                    else if (packetId == (int)ClientPackets.playerMovement)
                    {
                        int roomId = _packet.ReadInt();
                        int roundId = _packet.ReadInt();
                        float _x = _packet.ReadFloat();
                        float _y = _packet.ReadFloat();
                        EnqueueCurrent(slot => ServerHandle.PlayerMovement(slot, roomId, roundId, _x, _y));
                    }
                    else if (packetId == (int)ClientPackets.requestStart)
                    {
                        int roomId = _packet.ReadInt();
                        int roundId = _packet.ReadInt();
                        EnqueueCurrent(slot => slot.Room?.StartRound(slot, roomId, roundId));
                    }
                    else if (packetId == (int)ClientPackets.createRoom)
                    {
                        EnqueueCurrent(ServerHandle.CreateRoom);
                    }
                    else if (packetId == (int)ClientPackets.joinRoom)
                    {
                        int roomId = _packet.ReadInt();
                        EnqueueCurrent(slot => ServerHandle.JoinRoom(slot, roomId));
                    }
                    else if (packetId == (int)ClientPackets.leaveRoom)
                    {
                        int roomId = _packet.ReadInt();
                        EnqueueCurrent(slot => ServerHandle.LeaveRoom(slot, roomId));
                    }
                }
            }

            private void EnqueueCurrent(Action<Client> action)
            {
                ThreadManager.EnqueueAction(() =>
                {
                    Client slot = Server.clients[id];
                    if (slot.tcp == this) action(slot);
                });
            }

            private void ReceiveCallback(IAsyncResult _result)
            {
                try
                {
                    int _byteLength = stream.EndRead(_result);
                    if (_byteLength > 0) Server.Network.RecordTcpReceive(_byteLength);
                    if (_byteLength <= 0)
                    {
                        ThreadManager.EnqueueAction(() => Server.Disconnect(id, this));
                        return;
                    }

                    frameReader.Feed(receiveBuffer.AsSpan(0, _byteLength), HandleFrame);

                    stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
                }
                catch (Exception _ex)
                {
                    if (closed) return;
                    Console.WriteLine($"Loi nhan du lieu tu ID {id}: {_ex}");
                    ThreadManager.EnqueueAction(() => Server.Disconnect(id, this));
                }
            }
        }
        public class UDP
        {
            public volatile IPEndPoint? endPoint;
            internal byte[]? ProbeNonce { get; private set; }
            private readonly int id;

            public UDP(int _id)
            {
                id = _id;
            }

            public void Connect(IPEndPoint _endPoint)
            {
                ProbeNonce = ProtocolV2.NewProbeNonce();
                endPoint = _endPoint;
                Console.WriteLine($"[UDP] Client {id} UDP endpoint discovered: {endPoint}");
            }

            public void SendData(Packet _packet)
            {
                var recipient = endPoint;
                if (recipient != null) Server.SendUDPData(recipient, _packet);
            }

        }
    }
}
