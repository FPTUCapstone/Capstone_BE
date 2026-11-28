# Triển khai PDF hợp đồng trên Railway

## Mô hình

Backend giữ DOCX/PDF trong volume riêng. Một service `contract-renderer` riêng chỉ nhận DOCX qua HTTP nội bộ có Bearer token, trả ZIP gồm PDF và dữ liệu tọa độ. Backend tự kiểm tra lại dung lượng, PDF header, vị trí chữ ký và tính SHA-256. Cả PDF xem trước lẫn PDF chuẩn bị ký dùng chung adapter này. Các cổng API và quy tắc công bố/ký không thay đổi.

Renderer không cần database, Redis, Cloudinary, payOS, SMTP hoặc volume tài liệu của BE. Không gắn các shared variables chứa những bí mật đó vào service mới. Không gắn Docker socket. Service này không cần public domain. Unix IPC chỉ phục vụ các tiến trình LibreOffice trong container riêng, không có socket API/database trong container này.

Supervisor nhận HTTP; engine chạy dưới UID/GID `nobody`, với môi trường đã xóa bí mật và file descriptor được đóng. Launcher bắt buộc áp dụng Landlock ABI >= 3, seccomp chặn mọi socket ngoài Unix IPC, đọc tiến trình/điều khiển tiến trình, và giới hạn CPU, RAM, số tiến trình, kích thước file. Engine chỉ đọc thư viện/font và thông tin UID hệ thống cùng thư mục tác vụ riêng, không đọc source supervisor hay `/proc` chứa môi trường. Một tác vụ chuyển tại một thời điểm; toàn bộ cây tiến trình bị dừng sau deadline 50 giây. Service phải có giới hạn RAM/CPU cấp container để giới hạn tổng tài nguyên của các tiến trình con.

**Khả năng kernel phải được xác nhận trên host Railway thực tế.** Startup tự chạy kiểm tra cô lập; chỉ phục vụ `/health` sau khi thành công. Nếu log báo sandbox không khả dụng, không bật cờ bỏ qua, không chạy privileged, không nới seccomp: dùng host hỗ trợ hoặc worker cô lập tương đương. Kiểm thử Docker local không thay thế kiểm thử Railway.

Image dùng `tini` làm PID 1 để thu hồi cả tiến trình con mồ côi sau timeout, tránh tích tụ zombie. `RLIMIT_FSIZE` chỉ giới hạn từng file, không phải tổng dung lượng thư mục; theo dõi dung lượng đĩa tạm của worker trong vận hành. Mỗi tác vụ dùng thư mục riêng và được dọn khi kết thúc; không dùng đĩa tạm của renderer làm nơi lưu hợp đồng lâu dài.

## 1. Kiểm thử trước khi triển khai

Từ thư mục gốc BE:

```sh
python3 -m unittest discover -s docker/contract-renderer -p test_server.py
docker build -t tripmate-contract-renderer:railway -f docker/contract-renderer/Dockerfile.railway .
docker run --rm --memory 1g --cpus 1 --pids-limit 128 --security-opt no-new-privileges --mount type=bind,source="$PWD/docker/contract-renderer/smoke.py",target=/checks/smoke.py,readonly --entrypoint /usr/bin/tini tripmate-contract-renderer:railway -- python3 /checks/smoke.py
```

Lệnh shell ở trên dành cho Linux/CI. Trên PowerShell, thay `$PWD/docker/...` bằng đường dẫn tuyệt đối tới `smoke.py`. Test tạo DOCX tiếng Việt thật, nhận PDF, đọc chữ và anchor, kiểm tra xác thực, tài liệu lỗi, thiếu engine, timeout và dừng cây tiến trình. Startup kiểm tra quyền truy cập filesystem/mạng trong sandbox. Workflow `Contract renderer container` chạy cùng các bước này khi thay đổi renderer.

## 2. Tạo service renderer

Sau khi code đã được push/merge theo yêu cầu của người quản lý:

1. Railway, cùng project và environment với BE: tạo service từ repository BE, đặt tên `contract-renderer`. Chọn nhánh/commit chứa thay đổi này.
2. Root Directory giữ tại gốc repository. Cấu hình Dockerfile cho service mới bằng biến `RAILWAY_DOCKERFILE_PATH=docker/contract-renderer/Dockerfile.railway`. Backend vẫn dùng Dockerfile cũ tại gốc.
3. Chỉ thêm các biến của service mới:

   | Biến | Giá trị |
   |---|---|
   | `PORT` | `8080` |
   | `RENDERER_API_KEY` | Chuỗi ngẫu nhiên ít nhất 32 ký tự, tạo riêng cho renderer |

   Có thể tạo khóa 32 byte bằng trình quản lý mật khẩu, lưu vào Railway; không đưa khóa vào Git hoặc gửi trong ảnh/chat.

4. Một replica, giới hạn **1 vCPU / 1 GiB RAM** và healthcheck path `/health`. Không volume, không public domain, không custom start command.
5. Deploy. Chỉ tiếp tục khi log có `Contract renderer ready; sandbox self-test passed` và service healthy. Nếu thất bại, đọc lỗi startup; không bỏ cơ chế cô lập để vượt qua lỗi.

## 3. Cấu hình service Capstone_BE

Giữ nguyên volume mount `/data` và `ContractTemplates__Artifacts__RootPath=/data/contract-templates`. Thêm:

```text
ContractTemplates__Renderer__RemoteEndpoint=http://${{contract-renderer.RAILWAY_PRIVATE_DOMAIN}}:8080/render
ContractTemplates__Renderer__RemoteApiKey=${{contract-renderer.RENDERER_API_KEY}}
ContractTemplates__Renderer__TimeoutSeconds=60
ContractTemplates__PreviewWorkerEnabled=true
```

Railway phải resolve reference theo đúng tên service. Có thể dùng private hostname thực tế trong Settings -> Networking thay cho reference tên miền, ví dụ `http://contract-renderer.railway.internal:8080/render` khi đó đúng là hostname được Railway cấp.

Không đặt `ContractTemplates__Renderer__IsolatedExecutionVerified=true` cho phương án này. Remote endpoint được ưu tiên; không tự chuyển về local nếu service lỗi. Giữ cấu hình local trước đây để local tiếp tục dùng Docker. Việc chuẩn bị hợp đồng ký vẫn phụ thuộc `OperatorContracts__Enabled` và các migration signing đã áp dụng; không tự bật onboarding/signing gate trong lần sửa PDF này.

Deploy BE với commit chứa `RemoteContractTemplateRenderer`; image cũ không hiểu cấu hình mới. Không cần migration database mới cho thay đổi này.

## 4. Tạo lại PDF cho bản nháp

1. Mở mẫu hợp đồng, tải lại trang. Job đang Queued sẽ được preview worker xử lý theo chu kỳ khoảng 10 giây; job Processing hết lease có thể mất đến 5 phút để được lấy lại.
2. Nếu trạng thái Failed và còn lượt retry: nhấn **Tạo PDF xem trước**.
3. Nếu job đã hết 3 lượt hoặc không thể retry: tải DOCX nguồn, lưu thay đổi bản nháp bằng giao diện để tạo revision mới, rồi yêu cầu preview. Nếu mẫu đã công bố, tạo phiên bản nháp mới; không sửa bản đã công bố.
4. PDF phải hiển thị, chữ tiếng Việt đọc được, vùng chữ ký đúng. Sau khi xem và xác nhận đúng bản preview mới công bố theo luồng hiện có.
5. Kiểm tra hợp đồng TO được tạo từ mẫu đã công bố và vẫn xem được PDF. Không thực hiện ký thay người dùng.

## Chẩn đoán

Startup phân biệt rõ lỗi probe và ABI thấp. Ví dụ:

```text
Sandbox self-test failed: Landlock ABI probe failed: result=-1 errno=38 (Function not implemented); required>=3
Sandbox self-test failed: Landlock ABI unsupported: detected=2 required>=3
```

`detected=1/2` là ABI có thật nhưng thấp hơn yêu cầu. `errno=95` (EOPNOTSUPP) phù hợp trường hợp Landlock bị tắt; `errno=38` (ENOSYS) cho biết syscall không khả dụng với tiến trình, có thể do kernel hoặc chính sách runtime; `errno=1` (EPERM) cho biết thao tác bị từ chối. Chỉ mã lỗi không đủ để khẳng định cấu hình host Railway: giữ lại toàn bộ dòng để chẩn đoán tiếp, không tự bỏ kiểm tra. Không có token hoặc nội dung hợp đồng trong log này.

Native diagnostic tests có thể chạy độc lập bằng `docker build --target diagnostics-tests -f docker/contract-renderer/Dockerfile.railway .`.

- `401` từ renderer: khóa hai service không khớp.
- `429`: renderer đang xử lý một tác vụ khác; retry qua luồng hiện có sau khi tác vụ kết thúc, không tăng song song tùy tiện.
- `503`/startup unhealthy: engine, sandbox hoặc giới hạn runtime không đáp ứng. Đọc log startup; xác nhận image/host. Không trả PDF giả hoặc bỏ anchor check.
- Vẫn Processing: xác nhận preview worker bật, đúng DB, đúng commit; chờ lease cũ hết nếu worker trước đã dừng.
- Chỉ khi chức năng đã được kiểm thử trên Railway mới xác nhận production đã sửa xong.

## Khôi phục

Nếu dịch vụ mới chưa chạy được, tắt `ContractTemplates__PreviewWorkerEnabled` để ngừng nhận job preview mới ở worker, giữ nguyên artifact volume và bản hợp đồng đã công bố. Không công bố mẫu chưa có PDF hợp lệ. Khôi phục cấu hình renderer chỉ về một deployment đã kiểm chứng; không ghi đè trạng thái job trực tiếp bằng SQL.

## Tài liệu nền tảng

- [Railway private networking](https://docs.railway.com/networking/private-networking): kết nối giữa các service trong cùng environment.
- [Linux Landlock](https://docs.kernel.org/userspace-api/landlock.html): quyền filesystem hạn chế áp dụng cho tiến trình và con của nó; hỗ trợ thực tế được kiểm tra lúc chạy.
