# M4 · Độ ổn định kết nối và baseline protocol v3

**252/252 test Release đạt. Cả 9/9 lượt benchmark hợp lệ. Hiệu năng tick còn vấn đề mở trước khi đóng M4.** GameServer duy trì đủ 8 client Ready trong cả chín cửa sổ. Hai phòng Gameplay Active giữ 20 movement/s/client và peer khớp 28.800/28.800 movement. Trong hai trên ba lượt Active, tick interval lặp lại ở khoảng 47,5 ms. Tick rate Active gộp giảm từ 27,21 ở M3 xuống 23,08 tick/s ở M4 (−15,2%). Gameplay work trung bình lần lượt là 0,866 và 0,860 ms/tick.

## Cấu hình và cách tái lập

Ngày 22/11/2026. Build từ working tree sạch của `heartbeat_reliability@d177a0ff6545bbf802c17e0be4f3139e6db19f21`. Chạy trên Windows 11 x64 25H2 build 26200.9974, Intel Core i5-12450HX, 12 logical processors, RAM 11,71 GiB, power plan Balanced. SDK .NET 10.0.201, LoadClient báo runtime .NET 8.0.25. M2.5 và M3 ghi nhận cùng CPU, RAM, Windows build, power plan, SDK và runtime. Hai tiến trình chia sẻ máy, dùng IPv4 loopback. Tải nền, nhiệt độ, xung CPU và độ phân giải timer thực tế tại từng lượt chưa được ghi lại.

Rebuild toàn bộ `TinyBomber.sln` ở Release, một MSBuild worker, tắt shared compilation và dùng `--no-restore`. Chuyển `SolutionDir` của bước post-build sang thư mục riêng dưới `GameShared/obj/`. SHA-256 của DLL Unity trước build, sau build và sau benchmark trùng nhau. Các lượt chạy dùng DLL ở `bin/Release/net8.0` của hai project. Validator so hash từng bản sao server, GameShared và cấu hình đi kèm với manifest.

| Binary | SHA-256 |
| --- | --- |
| GameServer.dll | `448b6296fc3bf36ae1eb0aa81799b8086b188f903829bcd5e3447b0f21031259` |
| GameServer.LoadClient.dll | `ce99175d69f8ed53ceb57750828b60b402e6c05c970d74523729ab77ffbc0a62` |
| GameShared.dll Release của cả hai tiến trình | `7dda42c7b8cdca4d2450175bc96e88c8e052b56c70f8a6f281cb6ee6c89a9084` |
| GameShared.dll Unity đã nghiệm thu | `7bd842a96f3f1b473a56929e7ce4b2d8df8128e81af1dee1083b552faebb0970` |

Plugin Unity trùng byte với DLL Debug lưu trên máy và mang metadata build `a2b2426`. Các DLL Release vừa build mang metadata `d177a0f`. Các con số trong báo cáo áp dụng cho hai tiến trình Release. Kiểm thử Editor thuộc lần nghiệm thu trước.

Mỗi lượt khởi động một GameServer mới với `--max-connections 8`, bật metrics exporter, redirect stdout/stderr vào log riêng. LoadClient thiết lập đủ client và phòng rồi mới bắt đầu warm-up 10 giây, đo 60 giây. A dùng `lobby-idle` với 8 Ready và 0 phòng. B dùng `gameplay-idle` với 2 phòng Playing × 4 người. C dùng `gameplay-active`, cùng cấu hình phòng và mục tiêu 20 movement/s/client. Thứ tự ABC → BCA → CAB luân chuyển vị trí của từng kịch bản. Lượt C dùng hai lệnh sau.

```text
dotnet GameServer.dll --max-connections 8
dotnet GameServer.LoadClient.dll --server 127.0.0.1 --clients 8 --mode gameplay-active --movement-hz 20 --warmup-seconds 10 --seconds 60
```

## Regression và kiểm tra từng lượt

Build Release đạt 0 lỗi, 0 cảnh báo. Regression **252 passed, 0 failed, 0 skipped** trong 1 phút 14 giây. TRX có 9 ca TCP heartbeat, 9 ca UDP monitor, 13 ca `ProtocolV2SessionTests` và 3 ca disconnect/teardown. Suite kiểm deadline tại biên, Pong trùng/cũ, slot reuse, TCP im lặng, room lifecycle, wire v1/v2/v3 và đường nhận UDP khi lỗi. Socket fixture với LoadClient và peer mô phỏng chặn Probe, Echo hoặc cả hai chiều, kiểm degraded, phục hồi trong cùng round, trao đổi movement sau phục hồi. Chín lượt benchmark dùng GameServer thực và mạng loopback bình thường. [TCP heartbeat](../GameServer.Tests/TcpHeartbeatTests.cs) · [UDP monitor](../GameServer.Tests/UdpHealthMonitorTests.cs) · [Socket integration](../GameServer.Tests/ProtocolV2SessionTests.cs) · [Teardown](../GameServer.Tests/DisconnectTeardownTests.cs)

- **Điều kiện cũ đã xác định trước khi đo.** Trong bộ công cụ bên ngoài `capacity_commit3`, `run_baseline.py:167` yêu cầu UDP Rx delta bằng 0 khi mode khác C. `analyze_baseline.mjs:122` cũng yêu cầu cả TCP Rx/Tx và UDP Rx/Tx bằng 0 ở A/B. Bản sao v3 kiểm UDP có traffic hai chiều ở cả ba mode, giữ mode thật và tất cả kiểm tra client/phòng cũ. Analyzer kiểm cả bốn delta dương và UDP idle chia hết cho 88/84 byte. Bước kế tiếp đối chiếu số Echo mà client thực sự chấp nhận. Không sửa runner lịch sử.
- **JSON.** Đối chiếu UTC 60 giây, số client kết nối/Ready, mã lỗi, trạng thái chơi, phân bố phòng và các counter riêng của UDP health. Ở C, so sender/observer khác slot, cùng room/round, đổi tọa độ và peer quan sát đúng đích. So số thực gửi, số khớp và mục tiêu 9.600 riêng rẽ. LoadClient còn kiểm định danh room/round dự kiến trong vòng chạy. CSV phòng ghi phase và participants, không xuất round ID. [LoadClient](../GameServer.LoadClient/Program.cs)
- **CSV và log.** Chọn mọi snapshot trong cửa sổ UTC để kiểm Ready, room ID, phase, members, active/alive và participants. Ghép theo cả sample ID lẫn UTC. Chỉ lấy kỳ có hai snapshot liền kề trong cửa sổ để tính tick và byte. Đối chiếu counter lỗi đến snapshot cuối trong cửa sổ, snapshot đầu có lỗi và log đóng kết nối. Kiểm snapshot cuối và shutdown exporter. Log lỗi ghi socket không có UTC riêng nên việc định thời ở hai mép cửa sổ bị giới hạn bởi chu kỳ exporter.
- **Measured.** Nguồn đo là CSV server/phòng, counter hoàn tất gửi/nhận tại socket ứng dụng, log, working set và JSON của LoadClient. `UdpHealth` cộng dồn từ Ready đến ngay trước dispose, gồm warm-up. Ở C có 400 ms chờ observation sau khi ngừng gửi. Counter toàn phiên và delta của các kỳ CSV có phạm vi khác nhau.
- **Calculated.** Tick interval, sleep, non-sleep wall và gameplay work lấy trọng số theo số mẫu của từng metric. Tick/s = 1.000 / interval trung bình (ms). Gộp interval, sleep và non-sleep qua ba lượt theo số tick để giữ đẳng thức interval = sleep + non-sleep, gộp B/s theo tổng byte / tổng thời gian mạng. Các metric trung bình còn lại trong bảng so sánh là trung bình của ba lượt. CPU server lấy các cặp process sample phân biệt trong cửa sổ, chuẩn hóa theo 12 logical processors. RAM server là trung bình/max working set, RAM LoadClient lấy tại cuối cửa sổ. Queue lấy độ sâu tại snapshot, batch max bao phủ cả các tick giữa hai snapshot. [Thu thập tick](../GameServer/ServerMetrics.cs) · [CSV exporter](../GameServer/MetricsExporter.cs)


## Kết quả từng lượt

Cả 9 PID server khác nhau. Mỗi lượt chỉ chạy một lần. **0 lượt không hợp lệ**. Các lượt tick chậm vẫn nằm trong kết quả gộp. Mốc UTC dưới đây thuộc ngày 22/11/2026, làm gọn đến mili giây. Cửa sổ LoadClient đủ 60 giây cho từng lượt.

| Lượt | Bắt đầu → kết thúc UTC | Kỳ CSV / giây | Số tick | Interval TB / max (ms) | Tick/s | Sleep (ms) | Non-sleep (ms) | Gameplay (ms) | Queue TB / max | Batch max |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1-1-A | 08:14:59.901 → 08:15:59.901 | 58 / 58.999 | 1599 | 36.90 / 51.60 | 27.10 | 36.863 | 0.034 | 0.014 | 0.31 / 8 | 8 |
| 1-2-B | 08:16:14.357 → 08:17:14.357 | 58 / 59.182 | 1608 | 36.81 / 50.34 | 27.17 | 36.756 | 0.049 | 0.031 | 0.19 / 8 | 8 |
| 1-3-C | 08:17:28.739 → 08:18:28.739 | 58 / 59.056 | 1601 | 36.90 / 53.38 | 27.10 | 36.000 | 0.896 | 0.872 | 5.90 / 10 | 21 |
| 2-1-B | 08:18:43.602 → 08:19:43.602 | 58 / 59.129 | 1610 | 36.73 / 53.19 | 27.23 | 36.679 | 0.049 | 0.030 | 0.28 / 8 | 8 |
| 2-2-C | 08:19:58.110 → 08:20:58.110 | 57 / 58.781 | 1238 | 47.48 / 49.09 | 21.06 | 46.639 | 0.841 | 0.821 | 7.72 / 8 | 19 |
| 2-3-A | 08:21:12.677 → 08:22:12.677 | 57 / 58.495 | 1228 | 47.63 / 52.50 | 20.99 | 47.606 | 0.028 | 0.012 | 0.28 / 8 | 8 |
| 3-1-C | 08:22:27.298 → 08:23:27.298 | 57 / 58.554 | 1232 | 47.53 / 54.39 | 21.04 | 46.622 | 0.907 | 0.886 | 7.89 / 16 | 21 |
| 3-2-A | 08:23:42.487 → 08:24:42.487 | 57 / 58.381 | 1418 | 41.18 / 50.28 | 24.28 | 41.154 | 0.028 | 0.013 | 0.04 / 2 | 8 |
| 3-3-B | 08:24:56.845 → 08:25:56.845 | 58 / 59.198 | 1600 | 37.00 / 52.33 | 27.03 | 36.948 | 0.051 | 0.032 | 0.12 / 5 | 8 |

### Tài nguyên và traffic trong các kỳ CSV

WS là working set theo MiB, Rx/Tx theo góc nhìn server. Số byte và tổng thời gian mạng trước khi chia được lưu ở `verified-results.json`.

| Lượt | CPU server TB / max (%) | Server WS TB / max (MiB) | CPU client (%) | Client WS cuối (MiB) | TCP Rx / Tx (B/s) | UDP Rx / Tx (B/s) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1-1-A | 0.134 / 1.029 | 37.53 / 41.88 | 0.091 | 37.36 | 101.4 / 101.4 | 131.3 / 125.3 |
| 1-2-B | 0.094 / 0.522 | 39.90 / 44.97 | 0.074 | 38.91 | 101.1 / 101.1 | 130.9 / 124.9 |
| 1-3-C | 0.351 / 0.912 | 46.18 / 49.00 | 0.712 | 46.43 | 101.3 / 101.3 | 16783.6 / 48161.2 |
| 2-1-B | 0.128 / 0.913 | 39.69 / 44.50 | 0.059 | 38.14 | 101.2 / 101.2 | 131.0 / 125.0 |
| 2-2-C | 0.279 / 1.293 | 46.91 / 50.23 | 0.443 | 46.71 | 101.8 / 101.8 | 16777.1 / 48141.3 |
| 2-3-A | 0.112 / 0.652 | 37.53 / 41.73 | 0.076 | 37.53 | 102.3 / 102.3 | 132.4 / 126.4 |
| 3-1-C | 0.343 / 1.547 | 47.20 / 50.57 | 0.608 | 46.46 | 102.2 / 102.2 | 16771.2 / 48123.2 |
| 3-2-A | 0.112 / 0.646 | 37.90 / 42.01 | 0.067 | 37.48 | 102.5 / 102.5 | 132.6 / 126.6 |
| 3-3-B | 0.148 / 1.039 | 39.81 / 44.82 | 0.059 | 38.29 | 101.1 / 101.1 | 142.7 / 136.2 |

### Kết nối, health và gameplay

| Lượt | Ready / phòng Playing | Gửi / peer khớp (60 s) | Sender / có peer | Probe / Echo (toàn phiên) | TCP Rx / Tx (byte ÷ 68) | Lỗi gửi TCP/UDP trong kỳ | Lỗi gửi TCP/UDP cuối teardown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1-1-A | 8 / 0 | 0 / 0 | 0 / 0 | 104 / 104 | 88 / 88 | 0 / 0 | 0 / 0 |
| 1-2-B | 8 / 2 | 0 / 0 | 0 / 0 | 112 / 112 | 88 / 88 | 0 / 0 | 6 / 0 |
| 1-3-C | 8 / 2 | 9600 / 9600 | 8 / 8 | 112 / 112 | 88 / 88 | 0 / 0 | 6 / 0 |
| 2-1-B | 8 / 2 | 0 / 0 | 0 / 0 | 112 / 112 | 88 / 88 | 0 / 0 | 6 / 0 |
| 2-2-C | 8 / 2 | 9600 / 9600 | 8 / 8 | 112 / 112 | 88 / 88 | 0 / 0 | 6 / 0 |
| 2-3-A | 8 / 0 | 0 / 0 | 0 / 0 | 104 / 104 | 88 / 88 | 0 / 0 | 0 / 0 |
| 3-1-C | 8 / 2 | 9600 / 9600 | 8 / 8 | 112 / 112 | 88 / 88 | 0 / 0 | 6 / 0 |
| 3-2-A | 8 / 0 | 0 / 0 | 0 / 0 | 104 / 104 | 88 / 88 | 0 / 0 | 0 / 0 |
| 3-3-B | 8 / 2 | 0 / 0 | 0 / 0 | 112 / 112 | 88 / 88 | 0 / 0 | 6 / 0 |

Trong cả chín lượt, mỗi snapshot B/C có hai phòng Playing, mỗi phòng bốn thành viên Ready, bốn round participants và bốn người còn sống. Ở C, ví dụ của cả tám sender có round ID 1 và một observer cùng phòng. Ở B, định danh round được kiểm bởi LoadClient trong lúc chạy. JSON B không có movement example để đọc lại round ID từng client.

| Lượt C | Hz/client thực | Warm-up gửi / khớp | Đo gửi / khớp | Lag lịch gửi TB / max (ms) |
| --- | ---: | ---: | ---: | ---: |
| 1-3-C | 20.000 | 1600 / 1600 | 9600 / 9600 | 6.82 / 18.95 |
| 2-2-C | 20.000 | 1600 / 1600 | 9600 / 9600 | 8.09 / 16.32 |
| 3-1-C | 20.000 | 1600 / 1600 | 9600 / 9600 | 8.11 / 16.55 |

**Measured.** Cả chín lượt có tổng **984 Probe gửi và 984 Echo được chấp nhận** từ Ready đến trước dispose. Mọi trường ProbesExpired, Degradations, Recoveries, SendErrors, ReceiveErrors, DegradedClients và UnavailableSockets đều bằng 0. A ghi 104 Probe/lượt, B/C ghi 112/lượt. Thời gian toàn phiên gồm cả thiết lập phòng sau Ready, warm-up và phần chờ cuối của C. Cửa sổ đo workload vẫn dài như nhau. Tất cả nhóm lỗi trong `client.Errors` bằng 0.

**Calculated / inferred.** Mỗi lượt có 5.984 byte TCP Rx và 5.984 byte Tx ở những kỳ được giữ. Mỗi chiều tương đương 88 frame 68 byte. Workload không phát lệnh phòng trong kỳ đo, nên lưu lượng phù hợp với Ping/Pong. Counter byte không phân loại packet ID, sequence, số retry hay RTT. Với 8 phiên khỏe, nhịp danh nghĩa 5 giây tương ứng 108,8 B/s mỗi chiều TCP và 140,8/134,4 B/s UDP Rx/Tx. Các cụm packet ở biên kỳ có thể nằm ngoài delta. Idle thực đo khoảng 101–102 B/s mỗi chiều TCP và 132–135/126–129 B/s UDP khi gộp theo mode. Ở C, UDP chứa cả health lẫn movement. C tăng 134 B/s Rx và 120 B/s Tx so với M3. Không thể tách chính xác từng loại frame chỉ từ hai tổng byte Active.

**Deadline và recovery.** Server phát Ping ở giây 5, retry cùng challenge ở giây 10/15, hết hạn ở giây 20 tính từ Ready/Pong hợp lệ gần nhất. Client có watchdog 25 giây, chỉ Ping với sequence mới gia hạn. Probe UDP có hạn 5 giây, tối đa một Probe pending. Sau 15 giây thiếu Echo hợp lệ, monitor chuyển degraded. Một Echo phù hợp trước hạn của Probe hiện tại có thể phục hồi. Fake clock và socket integration kiểm các đường timeout và recovery. Các lượt baseline quan sát hoạt động liên tục qua nhiều chu kỳ và không ghi nhận lần degraded nào. [Server heartbeat](../GameServer/Client.cs) · [Client watchdog](../GameShared/HeartbeatWatchdog.cs) · [UDP monitor](../GameShared/UdpHealthMonitor.cs)

**Peer observation.** Mỗi input đo ở C khớp một lần với tọa độ mà peer được chỉ định cho sender quan sát trong cùng room/round. Fixture đối chiếu lịch sử giới hạn 512 input và chờ thêm 400 ms ở cuối. Tỷ lệ 28.800/28.800 mô tả bằng chứng gameplay của các peer này. Đo tỷ lệ datagram đến từng người nhận, retransmission ở tầng mạng hoặc RTT cần phép đo khác. [Movement trace](../GameServer.LoadClient/Movement.cs)

### Lỗi mạng khi đóng kết nối

- **Trong phép đo.** Tất cả snapshot đến và gồm snapshot cuối trong cửa sổ UTC đều có 0 lỗi gửi TCP/UDP. LoadClient báo 0 lỗi connect/send/receive/timeout/workload. Validator không thấy cảnh báo nhận trước shutdown, action thất bại hoặc snapshot bị bỏ.
- **Teardown.** A kết thúc với 0 lỗi. Mỗi B/C ghi 6 lỗi ghi TCP, tổng 36. Lỗi xuất hiện trong log sau dòng metrics cuối trong cửa sổ. Snapshot đầu ghi số lỗi dương ở cả sáu lượt đều mang thời điểm sau measurement và đã về 0 connection, 0 Ready, 0 phòng. Cả chín exporter báo `pending=0 dropped=0` và đóng CSV. Regression `ConcurrentDisconnectsReleaseBothRoomsAndTheirSlots` cũng ghi đúng 6 lỗi khi đóng đồng thời tám socket. Kết quả phù hợp với `WritePending` gặp peer đã đóng trong lúc dọn phòng. Chưa xác định packet đang ghi hay timestamp của từng lỗi ở khoảng giữa hai snapshot. [WritePending](../GameServer/Client.cs) · [Fixture đóng đồng thời](../GameServer.Tests/DisconnectTeardownTests.cs)

## Đối chiếu M2.5, M3 và M4

M2.5 là mốc lịch sử, M3 v2 là mốc so sánh trực tiếp. Tính lại sleep và non-sleep của cả ba mốc từ cùng các kỳ raw và cùng trọng số tick. Trung bình theo lượt của sleep trong báo cáo cũ có thể khác nhẹ khi mỗi lượt có số tick khác nhau.

| Mốc / mode | Interval (ms) | Tick/s | Sleep (ms) | Non-sleep (ms) | Gameplay (ms) | Queue TB / max | Batch max |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| M2.5 · A | 36.35 | 27.51 | 36.341 | 0.012 | 0.007 | 0.00 / 0 | 0 |
| M3 · A | 36.87 | 27.12 | 36.854 | 0.020 | 0.010 | 0.00 / 0 | 0 |
| M4 · A | 41.43 | 24.13 | 41.404 | 0.030 | 0.013 | 0.21 / 8 | 8 |
| M2.5 · B | 36.47 | 27.42 | 36.445 | 0.027 | 0.022 | 0.00 / 0 | 0 |
| M3 · B | 36.71 | 27.24 | 36.670 | 0.037 | 0.027 | 0.00 / 0 | 0 |
| M4 · B | 36.84 | 27.14 | 36.794 | 0.050 | 0.031 | 0.20 / 8 | 8 |
| M2.5 · C | 36.91 | 27.09 | 36.414 | 0.498 | 0.492 | 5.98 / 8 | 8 |
| M3 · C | 36.75 | 27.21 | 35.877 | 0.877 | 0.866 | 5.89 / 8 | 16 |
| M4 · C | 43.33 | 23.08 | 42.450 | 0.883 | 0.860 | 7.17 / 16 | 21 |

| Mốc / mode | Server CPU (%) / WS (MiB) | Client CPU (%) / WS cuối (MiB) | TCP Rx / Tx (B/s) | UDP Rx / Tx (B/s) | Gửi / khớp |
| --- | ---: | ---: | ---: | ---: | ---: |
| M2.5 · A | 0.096 / 34.90 | 0.014 / 33.76 | 0.0 / 0.0 | 0 / 0 | 0 / 0 |
| M3 · A | 0.101 / 35.27 | 0.035 / 33.90 | 0.0 / 0.0 | 0 / 0 | 0 / 0 |
| M4 · A | 0.119 / 37.65 | 0.078 / 37.45 | 102.1 / 102.1 | 132 / 126 | 0 / 0 |
| M2.5 · B | 0.068 / 37.43 | 0.022 / 34.61 | 0.0 / 0.0 | 0 / 0 | 0 / 0 |
| M3 · B | 0.119 / 37.46 | 0.017 / 34.57 | 0.0 / 0.0 | 0 / 0 | 0 / 0 |
| M4 · B | 0.123 / 39.80 | 0.064 / 38.45 | 101.1 / 101.1 | 135 / 129 | 0 / 0 |
| M2.5 · C | 0.225 / 45.92 | 0.417 / 44.81 | 0.0 / 0.0 | 14081 / 40310 | 28800 / 28800 |
| M3 · C | 0.363 / 45.40 | 0.590 / 45.39 | 0.0 / 0.0 | 16643 / 48022 | 28800 / 28800 |
| M4 · C | 0.324 / 46.76 | 0.587 / 46.53 | 101.8 / 101.8 | 16777 / 48142 | 28800 / 28800 |

### Tick chậm tái diễn theo thời gian

- **Measured.** Smoke trước đó (warm-up 2 s, đo 22 s) ghi interval/sleep/non-sleep **47,598/47,541/0,058 ms** ở Lobby và **45,852/44,328/1,524 ms** ở Active. Bản rebuild hiện tại mang metadata HEAD khác bản smoke. Chín lượt mới có cùng bộ binary Release và cùng cấu hình 10/60 s.
- **Measured.** Bốn lượt đầu có interval TB 36,73–36,90 ms. Ba lượt liên tiếp `2-2-C`, `2-3-A`, `3-1-C` đạt 47,48–47,63 ms. Trong mỗi lượt này, **57/57** kỳ CSV có interval trung bình ≥44 ms. `3-2-A` có **27/57** kỳ ở mức đó, rồi trở lại quanh 37 ms ngay trong cùng workload. Lượt B cuối đạt 37,00 ms. Ngưỡng mô tả 44 ms dùng để đếm kỳ chậm, không dùng loại mẫu. M4 có **198/518** kỳ như vậy, so với **0/521** ở M3 và **2/522** ở M2.5. Đây là trung bình theo kỳ khoảng một giây. CSV không đủ để tính p95/p99 từng tick.
- **Calculated.** So M3, tick/s gộp giảm **11,0% ở A**, **0,4% ở B** và **15,2% ở C**. Với cùng trọng số tick, ở Active interval tăng **6,578 ms**, sleep tăng **6,573 ms**, non-sleep tăng khoảng **0,006 ms**. Non-sleep bao gồm phần server xét heartbeat trước `ThreadManager.UpdateMain`, xử lý action, cập nhật room, capture và thời gian chờ scheduler ngoài lệnh sleep. CPU server Active là 0,324% so với 0,363% ở M3, trong khi queue TB/max mẫu tăng từ 5,89/8 lên 7,17/16, batch max tăng từ 16 lên 21. Thiết kế exporter và `ThreadManager` không có diff giữa hai HEAD. [UpdateLoop](../GameServer/Server.cs) · [UpdateMain và Thread.Sleep](../GameServer/ThreadManager.cs)
- **Inferred.** Thời gian thực trôi qua quanh `Thread.Sleep` giải thích phần lớn chênh lệch số học của interval. Snapshot đầu của `2-3-A` và `3-1-C` đã ghi lần lượt **47,26 và 47,37 ms với 0 connection**, trước khi LoadClient bắt đầu. Chậm theo từng đoạn thời gian, kể cả khi chưa có session, là lý do cần kiểm chứng điều kiện đánh thức thread trên máy. Số liệu hiện có chưa xác định tác nhân gây chậm hay chi phí riêng của heartbeat, UDP health, mã hóa hoặc scheduler.
- **Unverified / vấn đề mở.** Với 8 client cùng máy, workload movement vẫn đạt yêu cầu, nhưng target server 30 tick/s chưa đạt và mức quanh 21 tick/s tái diễn trong hai lượt Active. Trước khi quyết định đóng M4, đề xuất phép đo riêng xen kẽ binary M3 và M4 trên cùng máy, giữ chế độ nguồn và tải nền đã ghi, lấy thêm cửa sổ server trống trước/sau mỗi lượt, đối chiếu timer resolution và thời điểm thread thực sự được đánh thức. Cần phê duyệt riêng nếu gắn instrumentation. Kết quả hiện tại giới hạn ở loopback, hai phòng và movement quanh spawn. Chưa đo WAN, combat, số client lớn hơn hoặc chi phí riêng của Unity.

## Dữ liệu để phản biện và tái kiểm

Thư mục bằng chứng ngoài repository trên máy đo:

```text
D:\Workspace\Project\Game\TinyBomber-baselines\M4-v3-d177a0f-20261122
```

- `environment.json` ghi HEAD, cấu hình máy, thứ tự, tham số và hash. `regression/release.trx` giữ kết quả từng test. `runner-results.json` ghi kết quả sơ bộ. `verified-results.json` chứa metric mỗi lượt, kết quả đối chiếu, UTC, tổng byte, snapshot đầu có lỗi và snapshot cuối.
- Mỗi thư mục từ `1-1-A` đến `3-3-B` có `client.command.json`, `client.json`, `client.console.log`, `server.console.log`, `server_metrics.csv`, `room_metrics.csv` và bản sao binary server. Các CSV gốc ở thư mục con `server/metrics` có PID và định danh exporter trong tên file.
- Để kiểm lại số liệu đã có trên máy đo, chạy `python -B validate_v3.py` và `python -B tick_windows.py` từ thư mục bằng chứng. Validator dùng Python, Node và kiểm SHA-256 với bộ Release đã đo. Để tái lập phép đo, dùng bản source ở HEAD ghi trên, đặt `measure.py` và `run_baseline.py` vào thư mục raw mới, rồi chạy `python -B measure.py` khi hai cổng 26950/TCP và 26951/UDP đang trống. Raw M3 nằm ở thư mục ngang cấp `M3-v2-5ccc0c0-20261122`. [Baseline M3](M3-protocol-session-v2.md) · [Baseline M2.5](M2.5-capacity-baseline.md)

## Đối chứng M3/M4 và quyết định đóng M4

**Đóng M4 — Heartbeat & Connection Reliability.** M4 đạt các tiêu chí networking và regression đã kiểm thử. Bằng chứng gồm 252/252 test Release đạt, 9/9 lượt baseline hợp lệ và bốn lượt M4 trong phép đối chứng dưới đây. Tick chậm tái diễn trên cả M3 và M4. Chưa có bằng chứng về regression tick đặc thù M4 đủ để chặn nghiệm thu. **Mục tiêu 30 tick/s chưa đạt và được chuyển sang M8.**

Hai bản Release được build riêng từ `protocol_session@5ce02b9` và `heartbeat_reliability@8ca6cc9`, chạy trên cùng máy với .NET 8.0.25 qua loopback. Mỗi lượt có server process mới, 8 client, warm-up 10 giây và đo 60 giây. A là Lobby Idle. C là Gameplay Active với hai phòng bốn người, mục tiêu 20 movement/s/client. Mỗi workload chạy hai cặp, đảo thứ tự phiên bản ở cặp sau.

| Lượt thực chạy | Bản / tải | Interval TB (ms) | Tick/s ước tính | Sleep thực (ms) | Non-sleep (ms) | Gameplay work (ms) | Kỳ chậm / tổng kỳ CSV | Movement gửi / peer khớp |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | M3 / A | 36,983 | 27,040 | 36,962 | 0,021 | 0,011 | 0/57 | 0 / 0 |
| 2 | M4 / A | 36,772 | 27,194 | 36,740 | 0,032 | 0,014 | 0/58 | 0 / 0 |
| 3 | M4 / C | 38,965 | 25,664 | 38,024 | 0,941 | 0,922 | 7/58 | 9.600 / 9.600 |
| 4 | M3 / C | 41,852 | 23,894 | 40,557 | 1,295 | 1,285 | 26/58 | 9.600 / 9.600 |
| 5 | M4 / A | 41,917 | 23,857 | 41,883 | 0,034 | 0,015 | 15/57 | 0 / 0 |
| 6 | M3 / A | 44,783 | 22,330 | 44,759 | 0,024 | 0,012 | 45/57 | 0 / 0 |
| 7 | M3 / C | 36,498 | 27,398 | 35,817 | 0,681 | 0,672 | 0/58 | 9.520 / 9.520 |
| 8 | M4 / C | 36,952 | 27,062 | 36,050 | 0,902 | 0,882 | 0/58 | 9.600 / 9.600 |

**Measured.** CSV ghi thời gian thực quanh `Thread.Sleep`. Ở lượt 6, M3 dành 44,759 ms trong interval 44,783 ms cho sleep dù Lobby không có traffic trong các kỳ đo. Cả M3 và M4 đều có kỳ Lobby đạt 47,58 ms/tick. Bốn lượt M4 ghi tổng cộng 432 Probe gửi và 432 Echo được chấp nhận, không có Probe hết hạn hoặc phiên degraded. Lượt 7 của M3 chỉ gửi **9.520/9.600 movement mục tiêu**, đạt 19,83 Hz/client. Peer khớp cả 9.520 movement thực gửi. Sai lệch 80 thuộc số input phát ra, nên tải thực của cặp 7–8 khác nhau.

**Calculated.** Interval TB dùng trọng số số mẫu tick, tick/s bằng 1.000 chia interval TB tính theo ms. Non-sleep là interval trừ sleep thực. Một kỳ CSV được đếm là chậm khi interval trung bình trong kỳ đạt ít nhất 44 ms. Trong cặp Active 3–4 đạt đủ tải, interval M4 thấp hơn M3 2,887 ms. Khi đảo thứ tự ở cặp 7–8, interval M4 cao hơn 0,454 ms.

**Inference.** Chênh lệch Active đổi chiều giữa hai cặp. Các kỳ chậm xuất hiện ngay trên M3 v2, cả ở Lobby và trong round. Kết quả ủng hộ việc nghiên cứu cách scheduler chung chờ và thức dậy. Trong các kỳ có mẫu timer hệ thống, 2/68 kỳ chậm khi đọc được 1 ms và 48/164 kỳ chậm khi đọc được 15,625 ms. Đây là tương quan quan sát được.

**Unverified và giới hạn tái lập.** Chỉ có hai lượt mỗi tổ hợp phiên bản/workload trên một máy. Cả tám lượt dùng power plan Balanced và server có priority Normal tại các mẫu quan sát, nhưng CPU toàn máy trung bình từng lượt dao động từ 17,98% đến 25,91%. Mẫu timer lấy mỗi hai giây chỉ phản ánh giá trị hệ thống tại lúc đọc. Dữ liệu chưa xác định timer hiệu dụng của server, giá trị sleep yêu cầu từng tick hay độ trễ từ lúc thread có thể chạy đến lúc thực sự chạy. **Nguyên nhân gốc của tick chậm chưa được xác minh.**

**Phạm vi M8.** Trong cả hai bản, `ThreadManager.UpdateMain()` tính ngân sách nguyên 33 ms, trừ thời gian đã trôi qua khi xử lý queue và gọi sleep nếu phần còn lại dương. M8 cần đối chiếu *requested sleep* với *actual sleep* theo từng tick, đo scheduling jitter, đánh giá monotonic deadline tính từ đầu toàn bộ tick và kiểm tra chính sách bỏ nhịp sau stall để tránh phát tick dồn. [ThreadManager.UpdateMain](../GameServer/ThreadManager.cs) · [Server.UpdateLoop](../GameServer/Server.cs) · [ServerMetrics.BeginTick](../GameServer/ServerMetrics.cs)

Raw đối chứng trên máy đo nằm tại `D:\Workspace\Project\Game\TinyBomber-baselines\control-8ca6cc9-1795336330339`. Từng lượt có JSON, CSV và log. Dùng `results.txt` để đối chiếu tick, CPU, queue, traffic và movement, `timer-results.txt` để kiểm các kỳ có mẫu timer, `hashes.json` để nhận diện binary. Việc tái lập cần hai mốc source, bộ runner ngoài repository mà `control.py` tham chiếu và cùng cấu hình workload.
