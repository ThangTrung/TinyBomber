# M3 Protocol v2 — Cô lập phiên và baseline 8 client

Ngày chạy **22/11/2026**. Source runtime lấy từ `protocol_session@5ccc0c09de846263c95831b69b3772ac16be0387`. Working tree lúc build chỉ có thay đổi test. **200/200 test Release** và **9/9 lượt baseline v2** đạt. Ba lượt Gameplay Active giữ 20 movement/s/client, tổng cộng **28.800 gửi / 28.800 peer khớp**.

## 1. Kiểm thử cô lập phiên

| Ca kiểm thử | Kết quả quan sát |
| --- | --- |
| TCP action cũ đã enqueue | Fixture đưa một frame `createRoom` hợp lệ vào `Client.TCP.HandleFrame` của A, thu hồi A và cấp wrapper B vào đúng slot rồi mới drain queue. B vẫn Ready và chưa có phòng. Cùng frame qua wrapper B tạo phòng được. |
| UDP trên cùng slot và endpoint sau reconnect | Server cấp token mới. Registration mang token A không nhận probe. Registration với token B hoàn tất handshake. Confirm lặp không tạo `lobbyReady` thứ hai. Sau khi B vào round, fixture phát lại bytes của movement phiên A rồi gửi thêm movement từ cùng UDP socket, đúng room/round của B và mang token A. Peer không nhận position, TCP correction cho thấy server vẫn giữ tọa độ spawn. Movement với token B sau đó được peer nhận đúng. |
| LoadClient nhận probe và position phiên cũ | Fixture loopback chạy hai instance nối tiếp từ binary LoadClient Release. Probe A được gửi đến socket của B. B tiếp tục registration và chỉ gửi confirm với nonce của B. Khi đang Playing, handler nhận position cũ được gọi đồng bộ để kiểm trace trước, rồi fixture phát cả position cũ và position hiện hành qua UDP. Trace chỉ ghi nhận tọa độ từ token B và client vẫn Ready. |
| Regression | Giữ fixture wire v1/v2, deadline, timeout và slot reuse, room isolation, replay, TCP correction và fallback. Build Release 0 lỗi, 0 cảnh báo. VSTest 200 passed, 0 failed, 0 skipped. |

Ca queue dùng thứ tự do fixture kiểm soát. Socket test dùng cổng tự cấp cho GameServer và loopback `127.0.0.3` cho LoadClient vì binary này dùng cổng cố định 26950/26951. Ca LoadClient đọc binary sinh bởi build `TinyBomber.sln`; nên build solution trước khi chạy suite. Unity Editor và thử phát datagram cũ vào Unity nằm ngoài lượt kiểm chứng này.

Nguồn test [HandshakeDeadlineTests.cs](../GameServer.Tests/HandshakeDeadlineTests.cs) và [ProtocolV2SessionTests.cs](../GameServer.Tests/ProtocolV2SessionTests.cs).

## 2. Môi trường và bộ binary

- Windows x64 25H2 build 26200.9974, Intel Core i5-12450HX, 12 logical processors, 11.71 GiB RAM, power plan Balanced. SDK `10.0.201`. GameServer và LoadClient target `net8.0`, dùng cùng runtime config. LoadClient báo `.NET 8.0.25` ở cả chín lượt.
- Từng lượt khởi động GameServer riêng với `--max-connections 8`. GameServer và LoadClient là hai tiến trình chạy trên cùng máy qua IPv4 loopback `127.0.0.1`. Bật Metrics Exporter và redirect Console GameServer cùng cách với M2.5.
- Cả hai tiến trình dùng GameShared Release từ cùng build. Hash được kiểm lúc setup và đối chiếu lại sau khi đo. Bản sao GameServer theo từng lượt cũng được kiểm hash.

| Binary | SHA-256 |
| --- | --- |
| GameServer.dll | `748e1882d48b9c922d039754c36100663e5ce8ef0466718d1b7b811a49fc8506` |
| GameServer.LoadClient.dll | `71a2ea9d812795c005c6537474ff356a31fc3f302c78ce235e312d4d6acfbbab` |
| GameShared.dll của cả hai tiến trình | `69e305f36bc44520e7f34f3be7c18dad7579d3630e1cf4d257c01065349aa35d` |
| GameShared.dll Unity đã commit | `31f598968007e73fd76ae8d77e43922675418c34fed4b17b260cc1cbca712d2a` |

DLL Unity tracked khớp byte-for-byte với bản Debug có sẵn trước khi kiểm tra, và có hash khác bản Release dùng để đo. Rebuild Debug từ source hiện tại với đích post-build cách ly cho hash `b1ff4482859b9c827d5c1580b9c8212ac69f8f2a3655f3e97d4c2ae8c71e64b4`. So sánh IL của cả 77 phương thức và constructor cho kết quả trùng nhau. DLL tracked cũng có `ProtocolV2.Version=2`, token/nonce 16 byte và giới hạn username 64 byte. Các kiểm tra này xác nhận contract v2 trong DLL Unity đã commit. Hash DLL tracked giữ nguyên qua cả hai lần build.

## 3. Quy trình và điều kiện hợp lệ

Chạy **ABC → BCA → CAB**, warm-up 10 giây và đo 60 giây mỗi lượt.

| Kịch bản | Trạng thái kiểm bằng LoadClient và snapshot server |
| --- | --- |
| A · Lobby Idle | 8 connection và 8 Ready, 0 phòng, 0 movement. |
| B · Gameplay Idle | 8 Ready, hai phòng Playing × bốn người, 0 movement. |
| C · Gameplay Active | 8 Ready, hai phòng Playing × bốn người. Cả tám client gửi 20 movement/s, mỗi sender được một peer cố định cùng room/round quan sát. |

Mỗi lượt đạt khi LoadClient báo `Success=true` và tất cả nhóm lỗi bằng 0, thời lượng đo đúng 60 giây, đủ client/phòng trong các snapshot thuộc cửa sổ đo, CSV server và phòng khớp `sample_id`/timestamp và exporter đóng thành công. Cả chín lượt có snapshot cuối với 0 connection, 0 Ready và 0 phòng. Ở C, từng lượt có 9.600 gửi / 9.600 khớp trong measurement, 8/8 sender có peer khớp. Khoảng đo CSV giữ các kỳ có cả hai snapshot liền kề nằm trong cửa sổ UTC của LoadClient. Lượt `2-3-A` có 57 kỳ, tám lượt còn lại có 58 kỳ.

Lệnh mẫu cho C, chạy với binary Release. A và B lần lượt dùng `lobby-idle` và `gameplay-idle`.

```text
dotnet GameServer.dll --max-connections 8
dotnet GameServer.LoadClient.dll --server 127.0.0.1 --clients 8 --mode gameplay-active --movement-hz 20 --warmup-seconds 10 --seconds 60
```

Raw lưu ngoài repository tại **`D:\Workspace\Project\Game\TinyBomber-baselines\M3-v2-5ccc0c0-20261122`**. Thư mục gốc có `environment.json`. Mỗi thư mục lượt có `client.json`, `client.command.json`, `client.console.log`, `server.console.log`, `server_metrics.csv`, `room_metrics.csv` và bản sao binary GameServer dùng ở lượt đó.

## 4. Kết quả từng lượt

| Lượt | Kỳ CSV / giây | Tick interval TB / max (ms) | Tick/s ước tính | Gameplay / sleep TB (ms) | Queue TB / max mẫu | Batch max |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1-1-A | 58 / 59.099 | 36.95 / 52.30 | 27.06 | 0.010 / 36.932 | 0.00 / 0 | 0 |
| 1-2-B | 58 / 59.089 | 36.54 / 51.05 | 27.37 | 0.028 / 36.505 | 0.00 / 0 | 0 |
| 1-3-C | 58 / 59.096 | 36.46 / 50.14 | 27.43 | 0.806 / 35.645 | 6.07 / 8 | 8 |
| 2-1-B | 58 / 59.013 | 36.69 / 51.47 | 27.25 | 0.028 / 36.657 | 0.00 / 0 | 0 |
| 2-2-C | 58 / 59.100 | 36.80 / 53.05 | 27.17 | 0.913 / 35.877 | 5.52 / 8 | 8 |
| 2-3-A | 57 / 58.123 | 36.65 / 51.73 | 27.29 | 0.009 / 36.627 | 0.00 / 0 | 0 |
| 3-1-C | 58 / 59.274 | 37.00 / 52.53 | 27.02 | 0.880 / 36.112 | 6.07 / 8 | 16 |
| 3-2-A | 58 / 59.019 | 37.02 / 52.98 | 27.01 | 0.011 / 37.000 | 0.00 / 0 | 0 |
| 3-3-B | 58 / 59.172 | 36.89 / 53.48 | 27.11 | 0.027 / 36.849 | 0.00 / 0 | 0 |

| Lượt | CPU server TB / max (%) | RAM server TB / max (MiB) | CPU LoadClient (%) / RAM cuối (MiB) | UDP Rx / Tx (B/s) | Movement gửi / khớp | Lỗi ghi TCP sau cửa sổ |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1-1-A | 0.110 / 0.781 | 35.02 / 39.29 | 0.035 / 33.879 | 0 / 0 | 0 / 0 | 0 |
| 1-2-B | 0.114 / 0.522 | 37.45 / 42.41 | 0.013 / 34.602 | 0 / 0 | 0 / 0 | 6 |
| 1-3-C | 0.343 / 1.170 | 45.34 / 47.97 | 0.501 / 46.195 | 16655 / 48003 | 9.600 / 9.600 | 6 |
| 2-1-B | 0.143 / 0.645 | 37.45 / 42.40 | 0.022 / 34.555 | 0 / 0 | 0 / 0 | 6 |
| 2-2-C | 0.428 / 1.039 | 45.43 / 47.93 | 0.697 / 44.164 | 16640 / 48041 | 9.600 / 9.600 | 6 |
| 2-3-A | 0.096 / 0.518 | 35.61 / 39.79 | 0.024 / 33.906 | 0 / 0 | 0 / 0 | 0 |
| 3-1-C | 0.320 / 1.036 | 45.44 / 47.93 | 0.571 / 45.797 | 16633 / 48021 | 9.600 / 9.600 | 6 |
| 3-2-A | 0.098 / 0.649 | 35.18 / 39.43 | 0.046 / 33.906 | 0 / 0 | 0 / 0 | 0 |
| 3-3-B | 0.098 / 0.523 | 37.50 / 42.45 | 0.017 / 34.539 | 0 / 0 | 0 / 0 | 6 |

Cả chín cửa sổ ghi 0 byte TCP tăng thêm, 0 lỗi gửi TCP/UDP, 0 cảnh báo nhận và 0 snapshot bị bỏ. Sau cửa sổ đo, mỗi lượt B/C có sáu lỗi ghi TCP khi các client đóng đồng thời. Mọi lượt vẫn dọn hết connection và phòng, rồi đóng CSV với `pending=0 dropped=0`. Hiện chưa cô lập được packet TCP nào đang ghi khi peer đóng.

## 5. Đối chiếu với M2.5

Giá trị trình bày theo thứ tự **M2.5 → M3 v2**. Tham chiếu [baseline M2.5](M2.5-capacity-baseline.md).

| Kịch bản | Tick interval TB (ms) | Tick/s ước tính | Gameplay TB (ms) | Queue TB | Queue max mẫu |
| --- | ---: | ---: | ---: | ---: | ---: |
| A | 36.35 → 36.87 | 27.51 → 27.12 | 0.007 → 0.010 | 0.00 → 0.00 | 0 → 0 |
| B | 36.47 → 36.71 | 27.42 → 27.24 | 0.022 → 0.027 | 0.00 → 0.00 | 0 → 0 |
| C | 36.91 → 36.75 | 27.09 → 27.21 | 0.492 → 0.866 | 5.98 → 5.89 | 8 → 8 |

| Kịch bản | CPU server (%) | RAM server (MiB) | CPU LoadClient (%) | RAM LoadClient cuối (MiB) | UDP Rx (B/s) | UDP Tx (B/s) | Movement gửi / khớp ở cả hai baseline |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A | 0.096 → 0.101 | 34.90 → 35.27 | 0.014 → 0.035 | 33.76 → 33.90 | 0 → 0 | 0 → 0 | 0 / 0 |
| B | 0.068 → 0.119 | 37.43 → 37.46 | 0.022 → 0.017 | 34.61 → 34.57 | 0 → 0 | 0 → 0 | 0 / 0 |
| C | 0.225 → 0.363 | 45.92 → 45.40 | 0.417 → 0.590 | 44.81 → 45.39 | 14081 → 16643 | 40310 → 48022 | 28.800 / 28.800 |

Ở Active, gameplay work trung bình tăng từ **0.492 lên 0.866 ms**, CPU server từ **0.225% lên 0.363%** tổng năng lực CPU logic. Queue trung bình từ **5.98 xuống 5.89**, max mẫu vẫn là **8**. Tick/s ước tính từ **27.09 lên 27.21**. Sleep trung bình ở Active giảm từ **36.424 xuống 35.878 ms**. Trong cả hai baseline, sleep chiếm phần lớn interval quan sát được. Chênh lệch tick nhỏ và số lượt lặp hiện có chưa đủ kết luận scheduler nhanh hơn.

UDP Active tăng **18.2% Rx** và **19.1% Tx**. Hướng client-to-server tăng từ 88 lên 104 byte mỗi movement. Hướng server-to-client tăng từ 84 lên 100 byte mỗi position. Với 160 movement/s và ba người nhận mỗi movement, v2 dự kiến 16.640 B/s Rx và 48.000 B/s Tx ở tầng ứng dụng, gần với số đo. V2 tạo frame mã hóa riêng theo token của từng người nhận. Phép đo này ghi nhận chi phí toàn bộ runtime v2, chưa cô lập riêng AES, HMAC, cấp phát bộ nhớ, I/O hay biến thiên scheduler.

### Cách đọc metric

- **Đo trực tiếp.** Tick interval là thời gian bắt đầu tick kế tiếp trừ thời gian bắt đầu tick hiện tại. Gameplay work và sleep là thời lượng server lấy mẫu. Queue là độ sâu tại thời điểm snapshot, nên max mẫu chỉ bao phủ các snapshot.
- **Tổng hợp hoặc ước tính.** Tick/s tính bằng `1000 / tick interval trung bình có trọng số theo số mẫu`. Gameplay/sleep trong từng lượt dùng trọng số số mẫu. CPU server là CPU của tiến trình chuẩn hóa theo 12 logical processors trên các snapshot riêng biệt trong cửa sổ. RAM là working set, đo trung bình ở server và tại cuối cửa sổ ở LoadClient.
- **Mạng và gameplay.** Rx/Tx là delta counter byte tầng ứng dụng chia cho thời gian mạng tương ứng. Tx đếm hoàn tất ghi/gửi cục bộ. Movement sent và peer match lấy từ LoadClient. Peer match ghép theo room, round, sender và tọa độ trong lịch sử giới hạn. Tỷ lệ khớp mô tả quan sát của fixture, chưa xác định tỷ lệ mất gói trên mạng.
- **Giới hạn.** Kết quả áp dụng cho một máy loopback, 8 client, chuyển động quanh spawn và không có combat trong workload. CSV aggregate hiện có chưa đủ để rút p95/p99 từng tick. Baseline lịch sử dùng `ad4a999` với phần warm-up lúc chạy. So sánh này bao gồm toàn bộ thay đổi runtime giữa hai mốc và điều kiện môi trường tại từng ngày, nên chưa phải đối chứng cô lập chi phí kích hoạt v2.
