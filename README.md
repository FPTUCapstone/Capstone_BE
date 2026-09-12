# TripMate Backend

ASP.NET Core Web API cho TripMate, viết theo Clean Architecture. Phục vụ Next.js Admin Web
(`Capstone_FE`) và app Flutter cho Traveler/Tour Operator (`Capstone_Mobile`). Tài liệu sản phẩm
và yêu cầu (SRS, use case, business rules...) nằm ở repo riêng `Capstone_Docs`.

## 1. Kiến trúc

```
src/
  TripMate.Domain          Entity, enum — không phụ thuộc gì khác.
  TripMate.Application     Use case (MediatR command/query), validation, DTO, interface.
                            Chỉ phụ thuộc Domain.
  TripMate.Infrastructure  EF Core, JWT, password hashing — implement các interface của Application.
  TripMate.Api             Controller, middleware, composition root (Program.cs).
tests/
  TripMate.Application.UnitTests
  TripMate.Infrastructure.UnitTests
  TripMate.Api.IntegrationTests
```

Dependency luôn hướng vào trong: `Api` → `Application` + `Infrastructure`; `Infrastructure` →
`Application`; `Application` → `Domain`. `Domain` không phụ thuộc gì. `Application` không bao giờ
tham chiếu tới EF Core SqlServer provider, ASP.NET Core hay bất kỳ hạ tầng cụ thể nào — chỉ dùng
`Microsoft.EntityFrameworkCore` cho phần `DbSet<T>` của `IApplicationDbContext`.

Mỗi feature nằm dưới `Application/Features/<Feature>/<UseCase>/` dạng vertical slice tự chứa
(command/query + handler + validator) — xem feature `Authentication` (`Register`, `Login`) làm
mẫu tham khảo.

Database là **database-first**: schema thật nằm ở `database/*.sql`, không dùng EF Core migrations
(xem mục 6 và [`database/README.md`](database/README.md)).

## 2. Yêu cầu hệ thống (Prerequisites)

| Bắt buộc | Ghi chú |
| --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | Đã pin version qua `global.json`, không cần cài thêm gì khác để build/test. |
| Git | Để clone repo. |

| Chọn 1 trong 2 để chạy database | Ghi chú |
| --- | --- |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) **(khuyên dùng)** | Không cần cài SQL Server, `sqlcmd` hay gì thêm — mọi thứ chạy trong container. |
| SQL Server cài sẵn trên máy (LocalDB / Developer Edition) | Cần tự cài `sqlcmd` để áp schema tay. |

## 3. Chạy nhanh nhất — dùng Docker (khuyên dùng)

```bash
git clone git@github.com:FPTUCapstone/Capstone_BE.git
cd Capstone_BE
cp .env.example .env      # chỉ 1 lần đầu, chỉnh giá trị nếu cần
docker compose up -d --build
```

Lệnh trên tự làm hết 3 việc theo đúng thứ tự: dựng SQL Server container → áp schema
(`database/tripmate_schema_v7.sql`) → chạy API. Không cần tự canh thời gian chờ DB sẵn sàng.

Kiểm tra đã chạy đúng:

```bash
docker compose ps                 # 3 service: sqlserver, api phải "Up"; db-init "Exited (0)"
curl http://localhost:5000/health # {"status":"healthy"}
```

Mở Swagger UI tại `http://localhost:5000/swagger` để thử API trực tiếp trên trình duyệt, hoặc
test nhanh bằng `curl`:

```bash
curl -X POST http://localhost:5000/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"you@example.com","password":"Passw0rd123","fullName":"Your Name"}'

curl -X POST http://localhost:5000/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"you@example.com","password":"Passw0rd123"}'
```

Dừng lại: `docker compose stop` (giữ data) hoặc `docker compose down -v` (xoá sạch data, chạy lại
từ đầu). Chi tiết đầy đủ hơn về Docker, database, cách áp schema tay, xử lý lỗi thường gặp
(Docker chưa mở, port bận, kết nối bằng GUI tool...): xem **[`database/README.md`](database/README.md)**.

## 4. Gửi database (đã có sẵn schema) cho team qua file `.tar` — không cần chạy code

Nếu bạn chỉ muốn team có ngay 1 SQL Server **đã có sẵn `TripMateDb` + schema**, load xong là chạy
được luôn, không cần biết Docker Compose hay chạy `apply-schema.sh`: xem **mục 10 của
[`database/README.md`](database/README.md)**.

Tóm tắt cực nhanh (chi tiết + giải thích từng bước, số liệu thật đã verify: xem link trên):

```bash
# Người gửi — build 1 lần, xuất file (~600 MB). Truyền mật khẩu sa qua build-arg (lấy từ .env):
docker build -f database/Dockerfile.seeded -t tripmate-db:v7 --build-arg SA_PASSWORD="<SA_PASSWORD trong .env>" .
docker save -o tripmate-db.tar tripmate-db:v7

# Người nhận — load và chạy, có DB ngay, không cần bước nào khác
docker load -i tripmate-db.tar
docker run -d --name tripmate-sqlserver -p 14330:1433 tripmate-db:v7
```

Code (`src/`) bạn vẫn chạy trực tiếp bằng `dotnet run` như mục 5 bên dưới — cách này chỉ đóng gói
riêng phần database, đúng với việc bạn chỉ muốn chạy SQL trên Docker, còn lại chạy local.

## 5. Chạy không dùng Docker

1. Cài SQL Server (LocalDB hoặc Developer Edition) và có `sqlcmd`.
2. Cấp connection string cho API qua biến môi trường — **không** hardcode mật khẩu vào file đã
   commit (`appsettings.Development.json` để `ConnectionStrings:Default` trống):

   ```powershell
   # PowerShell (ASP.NET Core tự đọc biến môi trường, không cần chỉnh file nào)
   $env:ConnectionStrings__Default = "Server=localhost,14330;Database=TripMateDb;User Id=sa;Password=<SA_PASSWORD của bạn>;TrustServerCertificate=True;"
   ```

   Tùy chọn (cơ chế User Secrets mà `AGENTS.md` §5.4 nêu): chạy `dotnet user-secrets init --project src/TripMate.Api`
   một lần để bật, rồi `dotnet user-secrets set "ConnectionStrings:Default" "<chuỗi trên>" --project src/TripMate.Api`.
   Chạy qua Docker thì không cần bước này — `docker-compose.yml` tự set `ConnectionStrings__Default`
   từ `SA_PASSWORD` trong `.env`.
3. Áp schema tay:

   ```bash
   sqlcmd -S <server> -U sa -P <password> -d master -Q "CREATE DATABASE TripMateDb"
   sqlcmd -S <server> -U sa -P <password> -I -d TripMateDb -i database/tripmate_schema_v7.sql
   ```

   Cờ `-I` bắt buộc phải có (bật `QUOTED_IDENTIFIER`), thiếu sẽ lỗi khi tạo filtered index — xem
   giải thích ở `database/README.md` mục 4.

   ⚠️ Đây là project database-first — **không chạy** `dotnet ef migrations add` hay
   `dotnet ef database update`, project này không có và không nên có thư mục `Migrations`.

4. Chạy API:

   ```bash
   dotnet run --project src/TripMate.Api
   ```

5. Swagger UI ở `https://localhost:<port>/swagger` (môi trường Development).

## 6. Chạy test

```bash
dotnet test
```

Các bài kiểm thử tích hợp SQL Server được đánh dấu `Category=SqlServer`. Nếu chưa cấu hình database,
chúng được báo `Skipped` rõ ràng để bộ test nhanh không phụ thuộc máy cá nhân. Để chạy đầy đủ trên
SQL Server local, khởi động service `sqlserver`, rồi cấp connection string bằng biến môi trường;
không ghi mật khẩu vào source:

```powershell
docker compose up -d sqlserver
$env:TRIPMATE_SQLSERVER_TEST_CONNECTION = "Server=localhost,14330;Database=master;User Id=sa;Password=<local-sa-password>;Encrypt=False;TrustServerCertificate=True"
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter "Category=SqlServer"
```

Mỗi test tạo một database riêng tên `TripMate_Test_<guid>`, áp trực tiếp
`database/tripmate_schema_v7.sql`, và chỉ xóa đúng database tạm đó sau khi chạy. Nếu biến môi trường
đã được đặt nhưng SQL Server, credential hoặc schema có lỗi, test sẽ fail thay vì tự động skip.
Tài khoản trong connection string cần quyền tạo và xóa database test.

## 7. ⚠️ Database-First — không dùng EF Core migrations

`database/tripmate_schema_v7.sql` là **nguồn chân lý duy nhất** cho schema, áp bằng
`database/apply-schema.sh`. `TripMate.Infrastructure` map thủ công vào đó qua
`Persistence/Configurations/*Configuration.cs` — không có thư mục `Migrations`, và
`dotnet ef migrations add` / `dotnet ef database update` **không bao giờ được chạy** trong repo
này. Chi tiết đầy đủ, gồm 2 lỗi dễ dính đã gặp và fix (đã verify thật trên DB thật, không phải
đoán): xem [`database/README.md`](database/README.md) mục 9 và `AGENTS.md`.

## 8. Thêm một feature mới (vertical slice)

1. Tạo `src/TripMate.Application/Features/<Feature>/<UseCase>/` gồm `Command`/`Query`,
   `Validator`, `Handler`. Dùng `Result`/`Result<T>` cho các lỗi nghiệp vụ mong đợi (business
   failure); chỉ `throw` cho tình huống thật sự bất thường/không lường trước.
2. Nếu cần lưu trạng thái mới: thêm bảng vào `database/tripmate_schema_v7.sql` (hoặc tạo file
   `database/tripmate_schema_vN.sql` mới — xem changelog ở đầu file để theo đúng convention), rồi
   tự viết `Domain` entity và `IEntityTypeConfiguration<T>` tương ứng trong
   `TripMate.Infrastructure/Persistence/Configurations/`, map từng cột tường minh
   (`.HasColumnName(...)`, dùng `AsUtcDateTime2()` cho cột `DateTimeOffset` — xem
   `Persistence/Common/PropertyBuilderExtensions.cs`). Không tạo EF Core migration.
3. Thêm action trong controller ở `src/TripMate.Api/Controllers/V1/`, gửi command/query qua
   `ISender` và map `Result` lỗi bằng `HandleFailure` (xem `ApiControllerBase`).
4. Viết test cho validator và handler trong `tests/TripMate.Application.UnitTests/`.

Không tự ý thêm project/layer/NuGet package mới để giải quyết việc mà cấu trúc hiện tại đã làm
được — giữ đúng pattern vertical slice xuyên suốt mọi feature.

## 9. Cấu hình (`appsettings.json`)

| Key | Ý nghĩa |
| --- | --- |
| `ConnectionStrings:Default` | Connection string SQL Server. |
| `Jwt:Issuer` / `Jwt:Audience` | Dùng để validate JWT claim. |
| `Jwt:SigningKey` | Khoá đối xứng (base64) ký access token. **Không tái sử dụng giá trị trong `appsettings.Development.json` ngoài môi trường local.** |
| `Jwt:AccessTokenLifetimeMinutes` | Thời hạn access token. |
| `Cors:AllowedOrigins` | Domain được phép gọi API từ trình duyệt (Next.js admin web). |

`appsettings.Development.json` đã có sẵn giá trị placeholder cho môi trường local nên chạy được
ngay sau khi clone. Mọi môi trường chia sẻ/triển khai thật phải tự cấp secret riêng qua biến môi
trường hoặc secret manager — **không bao giờ commit secret thật**.

### 9.1 Firebase Admin credentials (bắt buộc cho đăng nhập Firebase)

Các flow xác thực bằng Firebase ID token (`/api/v1/auth/register`, `/api/v1/auth/verify-email`,
`/api/v1/auth/google`) xác minh token qua **Firebase Admin SDK**. Backend là nguồn chân lý duy
nhất cho việc xác minh token — không có cơ chế fallback nào. Vì vậy:

- **Có credential** → token hợp lệ được xác minh và đăng nhập hoạt động bình thường.
- **Thiếu credential** → API vẫn chạy, nhưng mọi Firebase ID token bị **từ chối** (fail-closed)
  với lỗi xác thực; log khởi động sẽ ghi rõ Firebase verification chưa khả dụng.

**Cách lấy file credential (developer tự làm, không ai gửi qua chat/git):**

1. Vào [Firebase console](https://console.firebase.google.com/) → chọn project
   (`tripmate-82be3`) → ⚙️ **Project settings** → **Service accounts** → **Generate new private
   key** → tải về file JSON.
2. Lưu file tại `secrets/tripmate-firebase-admin.json` (thư mục `secrets/` ở repo root — đã được
   `.gitignore` bỏ qua, **tuyệt đối không commit**).

**Docker:** `docker-compose.yml` mount `./secrets` vào container ở chế độ read-only
(`/run/secrets`) và đặt `GOOGLE_APPLICATION_CREDENTIALS=/run/secrets/tripmate-firebase-admin.json`.
Chỉ cần đặt file đúng chỗ rồi `docker compose up` — không cần chỉnh gì thêm.

**Local (`dotnet run`, không dùng Docker):** đặt biến môi trường
`GOOGLE_APPLICATION_CREDENTIALS` trỏ tới file JSON trên máy, ví dụ:

```bash
# PowerShell
$env:GOOGLE_APPLICATION_CREDENTIALS = "C:\secrets\tripmate-firebase-admin.json"
dotnet run --project src/TripMate.Api
```

Hoặc dùng User Secrets để tránh set biến môi trường mỗi phiên:

```bash
dotnet user-secrets set "GOOGLE_APPLICATION_CREDENTIALS" "C:\secrets\tripmate-firebase-admin.json" --project src/TripMate.Api
```

Cách thay thế (không dùng file): dán nội dung JSON vào biến môi trường
`FIREBASE_SERVICE_ACCOUNT_KEY_JSON` (hoặc config key `Firebase:ServiceAccountKeyJson`) — cũng
không bao giờ commit. Thứ tự ưu tiên credential: JSON inline → `GOOGLE_APPLICATION_CREDENTIALS`
→ Application Default Credentials.

## 10. Xử lý sự cố thường gặp

| Triệu chứng | Cách xử lý |
| --- | --- |
| `docker compose up` lỗi "cannot connect to the Docker daemon" | Mở Docker Desktop lên, đợi tới khi chạy hẳn rồi thử lại. |
| Port `5000` đã bị chiếm | Đổi port map service `api` trong `docker-compose.yml`. |
| Port DB (`14330`) đã bị chiếm | Đổi `DB_HOST_PORT` trong `.env`. |
| Chạy `dotnet run` local (DB trong Docker) mà API báo 500 `Login failed for user 'sa'`, dù `db-init` báo áp schema thành công | **Không phải sai mật khẩu** — máy bạn có SQL Server cài native đang chiếm port `1433`, khiến kết nối từ host bị lạc sang đó thay vì vào container (container không dùng port `1433` của host, dùng `14330` — xem `database/README.md` mục 8 để chẩn đoán chính xác). |
| Build lỗi thiếu SDK | Kiểm tra `dotnet --version` khớp với `global.json` (SDK 10.x). |
| Gọi API bị 401/403 dù đăng nhập đúng | Kiểm tra `Jwt:SigningKey`/`Jwt:Issuer`/`Jwt:Audience` giữa lúc phát hành token và lúc validate có khớp không (đặc biệt nếu chạy nhiều instance API với config khác nhau). |
| Đăng nhập bằng Firebase (register / verify-email / Google) luôn bị từ chối | Thiếu Firebase Admin credential — xem **mục 9.1**: đặt service-account JSON tại `secrets/tripmate-firebase-admin.json` rồi chạy lại. Không có credential thì mọi Firebase ID token bị từ chối (fail-closed, cố ý). |
| Các lỗi liên quan tới database, Docker container, schema | Xem bảng đầy đủ hơn ở [`database/README.md`](database/README.md) mục 8. |

## 11. Quy trình làm việc nhóm

Xem [`CONTRIBUTING.md`](CONTRIBUTING.md) (branch, commit convention, Pull Request) và
[`AGENTS.md`](AGENTS.md) (quy tắc kiến trúc bắt buộc, đặc biệt là phần database-first) trước khi
đóng góp code.
