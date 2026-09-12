# Database TripMate — Hướng dẫn Setup & Chạy trên Docker

Thư mục này chứa schema vật lý chính thức của TripMate (`tripmate_schema_v7.sql`) và script để
áp schema đó vào container SQL Server. Đọc kỹ trước khi đụng vào database.

## 1. Thư mục này có gì

| File                       | Vai trò                                                                                                                                                                                                                                                                                               |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `tripmate_schema_v7.sql` | Toàn bộ DDL: 8 schema (`dbo`, `catalog`, `commerce`, `planning`, `trip`, `payment`, `commercial`, `social`), ~56 bảng, seed data cho `SystemConfigs` và `Messages` (130 message thật từ SRS §5.3, MSG01–MSG130).                                                           |
| `tripmate_schema_v6.sql` | Bản trước, giữ lại theo đúng convention versioning của file (mỗi lần đổi schema ghi changelog ở đầu file thay vì sửa đè) — không dùng để chạy, chỉ để tham chiếu lịch sử.                                                                                               |
| `apply-schema.sh`        | Tạo database`TripMateDb` nếu chưa có, rồi áp `tripmate_schema_v7.sql` vào. Chạy lại nhiều lần vẫn an toàn — tự động bỏ qua nếu v7 đã được áp rồi, và **báo lỗi rõ ràng** (không âm thầm bỏ qua) nếu phát hiện DB đang có bản cũ hơn (ví dụ v6). |

## 2. Yêu cầu trước khi bắt đầu

- Đã cài [Docker Desktop](https://www.docker.com/products/docker-desktop/) và **đang mở/chạy**
  (icon cá voi ở khay hệ thống chuyển sang trạng thái "Running").
- Đã clone repo `Capstone_BE`.
- Chưa có gì khác cần cài thêm — không cần cài SQL Server, sqlcmd, hay `dotnet-ef` để chạy DB.

Kiểm tra Docker đã sẵn sàng chưa:

```bash
docker info
```

Nếu lệnh trên báo lỗi kiểu "cannot connect to the Docker daemon" → mở ứng dụng Docker Desktop lên,
đợi vài chục giây tới khi icon chuyển "Running" rồi thử lại.

## 3. Bắt đầu nhanh (khuyên dùng — tự động hoàn toàn)

```bash
cd Capstone_BE
cp .env.example .env      # chỉ làm 1 lần đầu tiên, chỉnh giá trị nếu cần
docker compose up -d
```

Chỉ vậy thôi. `docker compose` sẽ tự khởi động lần lượt theo đúng thứ tự phụ thuộc:

1. **`sqlserver`** — container SQL Server 2022, chờ tới khi healthy (healthcheck tự ping DB).
2. **`db-init`** — chạy `apply-schema.sh` đúng 1 lần rồi thoát (exit code 0 = thành công). Bước
   này chỉ chạy sau khi `sqlserver` đã healthy.
3. **`api`** — chỉ khởi động sau khi `db-init` báo thành công.

Nói cách khác, bạn không cần tự canh thời gian chờ SQL Server khởi động rồi mới chạy API — Compose
lo hết phần đó.

Kiểm tra mọi thứ đã lên đúng:

```bash
docker compose ps                 # cả 3 service phải "Up" (db-init sẽ show "Exited (0)")
docker compose logs db-init       # phải thấy "Schema applied successfully."
curl http://localhost:5000/health # phải trả {"status":"healthy"}
```

Chạy `docker compose up -d` lần thứ 2 trở đi, `db-init` sẽ in ra
`TripMate schema (v7) already present — skipping.` — đây là điều **bình thường**, không phải lỗi.

Nếu DB đang có bản **cũ hơn v7** (ví dụ bạn đã chạy stack này từ trước khi có v7), `db-init` sẽ
**báo lỗi và dừng lại** thay vì âm thầm bỏ qua hoặc chạy đè gây lỗi "object already exists" — làm
theo hướng dẫn trong thông báo lỗi đó (reset volume, xem mục 5).

## 4. Áp schema thủ công (không cần restart cả stack)

Dùng khi bạn chỉ mới rebuild lại container `sqlserver` và muốn áp (lại) schema bằng tay:

```bash
docker compose exec sqlserver bash /scripts/apply-schema.sh
```

Hoặc chạy thẳng file `.sql` bằng `sqlcmd` bên trong container:

```bash
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -C -I -S localhost -U sa -P "$SA_PASSWORD" -d TripMateDb \
  -i /scripts/tripmate_schema_v7.sql
```

Cờ `-I` quan trọng: nó bật `QUOTED_IDENTIFIER` cho session. Thiếu cờ này, 4 unique/filtered index
trong script (`UX_Users_Email`, `UX_Users_Phone`, `UX_Itineraries_ShareToken`,
`UX_TicketQrCodes_OneActivePerTicket`) sẽ tạo lỗi `Msg 1934` — `sqlcmd` mặc định tắt setting này,
khác với SSMS tự bật sẵn. `apply-schema.sh` đã có sẵn cờ này rồi; chỉ cần quan tâm nếu bạn tự gõ
`sqlcmd` tay.

## 5. Làm sạch lại từ đầu (reset database)

```bash
docker compose down -v   # -v = xoá luôn volume dữ liệu SQL Server — MẤT HẾT DATA hiện có
docker compose up -d
```

⚠️ `-v` xoá vĩnh viễn volume `tripmate-sqlserver-data` — chỉ dùng khi bạn thật sự muốn database về
trạng thái trống sạch (ví dụ sau khi đổi schema, hoặc data test bị rối). Nếu chỉ muốn dừng container
mà giữ nguyên data, dùng `docker compose down` (không có `-v`) hoặc `docker compose stop`.

## 6. Xác minh schema đã lên đúng

```bash
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -C -S localhost -U sa -P "$SA_PASSWORD" -d TripMateDb \
  -Q "SELECT s.name AS schema_name, COUNT(*) AS tables FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id GROUP BY s.name ORDER BY s.name;"
```

Kết quả kỳ vọng: `catalog` (8), `commerce` (13), `commercial` (4), `dbo` (11), `payment` (4),
`planning` (3), `social` (5), `trip` (8) — tổng 56 bảng.

Kiểm tra riêng bảng message catalog đã có đủ 130 message thật (không phải 5 dòng placeholder cũ):

```bash
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -C -S localhost -U sa -P "$SA_PASSWORD" -d TripMateDb \
  -Q "SELECT COUNT(*) AS total_messages FROM dbo.Messages;"
```

Kết quả kỳ vọng: `130`.

## 7. Kết nối bằng công cụ GUI (Azure Data Studio / DBeaver / SSMS)

Nếu muốn xem/duyệt dữ liệu bằng tay thay vì `sqlcmd`, dùng thông tin kết nối sau (giá trị mặc định
lấy từ `.env.example`, chỉnh lại nếu bạn đổi trong `.env`):

| Trường                           | Giá trị                                                                    |
| ---------------------------------- | ---------------------------------------------------------------------------- |
| Server / Host                      | `localhost`                                                                |
| Port                               | `14330` (giá trị `DB_HOST_PORT` trong `.env`, không phải `1433`) |
| Authentication                     | SQL Login                                                                    |
| User                               | `sa`                                                                       |
| Password                           | giá trị `SA_PASSWORD` trong `.env` (xem `.env.example`)     |
| Database                           | `TripMateDb`                                                               |
| Encrypt / Trust server certificate | Bật "Trust server certificate" (container dùng self-signed cert)           |

Container phải đang chạy (`docker compose ps` thấy `sqlserver` là `Up`) thì mới kết nối được.

## 8. Các lỗi thường gặp

| Triệu chứng                                                                                                                                                                            | Nguyên nhân & cách xử lý                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `docker compose up` báo "cannot connect to the Docker daemon"                                                                                                                         | Docker Desktop chưa mở. Mở app lên, đợi icon "Running" rồi chạy lại.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `sqlserver` mãi không "Healthy", `db-init` không bao giờ chạy                                                                                                                   | Port`14330` (giá trị `DB_HOST_PORT`) trên máy bạn đang bị chương trình khác chiếm. Đổi `DB_HOST_PORT` trong `.env` sang port khác.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| Chạy code local (`dotnet run`, ngoài Docker) gọi API bị lỗi 500, log báo `Login failed for user 'sa'` — trong khi `db-init` log lại báo áp schema **thành công** | Đây**không phải sai mật khẩu**. Máy bạn có SQL Server cài native (Windows service `sqlservr.exe`) đang chiếm sẵn port `1433` trên host — kiểm tra bằng `netstat -ano \| findstr :1433`, nếu thấy 2 dòng LISTENING là đúng tình huống này. `db-init` connect qua mạng nội bộ Docker (`sqlserver,1433`) nên không bị ảnh hưởng, nhưng `dotnet run` chạy trên host lại nối nhầm vào SQL Server native đó thay vì container. Repo đã mặc định map SQL Server container ra port `14330` (không phải `1433`) đúng để tránh việc này — kiểm tra `ConnectionStrings:Default` trong `appsettings.Development.json` có đang trỏ đúng `localhost,14330` không, và `.env` có `DB_HOST_PORT=14330` khớp với nó không. |
| `db-init` báo lỗi `Msg 1934` khi tạo index                                                                                                                                        | Đang tự chạy`sqlcmd` tay mà quên cờ `-I` (xem mục 4) — `apply-schema.sh` đã tự có cờ này nên không gặp lỗi này nếu chạy qua script.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| Sửa nội dung trong`tripmate_schema_v7.sql` xong chạy `docker compose up -d` mà không thấy áp lại                                                                             | Nếu bảng`catalog` và `MSG130` đều đã tồn tại, script coi như "đã áp v7 rồi" và bỏ qua — sửa nội dung *bên trong* v7 không tự động được nhận diện là bản mới. Reset volume (mục 5) để áp lại từ đầu, hoặc tạo hẳn `tripmate_schema_v8.sql` mới theo đúng convention nếu đó là một thay đổi thật sự (xem `AGENTS.md`).                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `db-init` báo lỗi "has an older schema version applied"                                                                                                                              | DB đang có bản cũ hơn v7 (ví dụ v6) từ trước. Đây là chủ đích — script từ chối chạy đè lên schema cũ để tránh lỗi "object already exists" nửa chừng. Reset volume theo mục 5 rồi chạy lại.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| Trên Windows, sau khi sửa`apply-schema.sh` thì chạy báo lỗi khó hiểu trong container                                                                                           | Editor lưu file với line ending CRLF thay vì LF, script Linux không chạy được. Repo đã có`.gitattributes` ép LF cho `.sh`/`.sql` — đảm bảo bạn không tắt/bypass nó, và nếu tự tạo file `.sh` mới nhớ lưu LF.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| Muốn xem log chi tiết một service                                                                                                                                                     | `docker compose logs -f <tên-service>` (ví dụ `docker compose logs -f sqlserver`, `docker compose logs -f api`).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| Muốn dừng hẳn mà giữ data để hôm sau chạy tiếp                                                                                                                                 | `docker compose stop` (khác với `down -v` — không xoá container lẫn volume).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |

## 9. ⚠️ Quan trọng — đây là Database-First, không dùng EF Core migrations

`tripmate_schema_v7.sql` là **nguồn chân lý duy nhất** cho schema. `TripMate.Infrastructure` map
thủ công vào đó — `Domain/Entities/User.cs`, `RefreshToken.cs`, và các class
`IEntityTypeConfiguration<T>` trong `Persistence/Configurations/` phản ánh đúng từng cột của
`dbo.Users` và `dbo.RefreshTokens` (tên cột snake_case, khoá `BIGINT`, mọi cột `DATETIME2` được
map qua `AsUtcDateTime2()`).

Repo **không có** thư mục `Migrations`, và không nên thêm lại — **tuyệt đối không chạy
`dotnet ef migrations add` hay `dotnet ef database update`** trong project này. Khi cần đổi bảng
hoặc thêm bảng mới: sửa/thêm file `.sql` ở đây trước, rồi mới cập nhật tay `Domain` entity và
configuration tương ứng cho khớp. Xem `AGENTS.md` để biết đầy đủ quy tắc, gồm 2 lỗi dễ dính đã gặp
và fix (đều đã verify thật trên schema này, trên chính Docker Compose stack này, và trên database
thật — không phải đoán):

- `DATETIME2` không có offset, nên property `DateTimeOffset` bắt buộc phải qua `AsUtcDateTime2()`,
  nếu không `SqlClient` sẽ ném `InvalidCastException` khi đọc lại.
- Khoá `BIGINT IDENTITY` vẫn còn là giá trị mặc định CLR (`0`) cho tới khi `SaveChangesAsync` chạy
  xong — thứ gì cần dùng giá trị thật (claim `sub` trong JWT, một FK copy tay) chỉ được đọc **sau**
  khi save; và một FK trỏ tới row mới khác tạo trong cùng transaction phải gán qua navigation
  property, không copy `Id`.

## 10. Đóng gói SQL Server đã có sẵn schema qua file `.tar` — hướng dẫn từ đầu đến cuối

**Phạm vi của mục này chỉ là database.** Docker trong toàn bộ quy trình này **chỉ dùng để chạy SQL
Server** — code (`.NET`) không hề đóng gói qua Docker ở đây, luôn chạy trực tiếp bằng `dotnet run`
(hoặc F5 trong Visual Studio/Rider) trên máy mỗi người, và sau này đưa thẳng lên production/cloud
theo cách riêng (không liên quan gì tới file `.tar` hay image ở mục này).

**Phân vai:**

- **Leader (1 người)** — build image, xuất file `.tar`, gửi cho team. Làm mục 10.2 → 10.4.
- **Thành viên team** — chỉ cần nhận file, load, chạy container. Làm mục 10.5 → 10.7. Không cần
  biết `Docker Compose`, không cần chạy `apply-schema.sh`, không cần cài `sqlcmd`.

Toàn bộ lệnh dưới đây đã được tự tay build, chạy, đóng gói, xoá, nạp lại và verify thật (không
phải suy đoán) — số liệu (604 MB, 56 bảng, 130 message) là số đo thật.

### 10.1 Điểm xuất phát — chỉ có file `.sql`

Giả sử bạn chỉ có đúng 1 file `database/tripmate_schema_v7.sql` (DDL thuần, không tự chạy được).
Để biến nó thành 1 image Docker "chạy phát ăn ngay", cần thêm 2 file cấu hình (repo đã có sẵn,
không cần tạo lại — nhưng hiểu vai trò của chúng để biết mình đang làm gì):

**`database/seed-image.sh`** — script chỉ chạy **lúc build image** (không chạy lúc container khởi
động sau này). Việc nó làm: tự khởi động tạm 1 tiến trình SQL Server, chờ nó sẵn sàng, tạo database
`TripMateDb`, áp file `.sql` vào, rồi tắt SQL Server đi.

**`database/Dockerfile.seeded`** — định nghĩa cách build: bắt đầu từ image SQL Server gốc
(`mcr.microsoft.com/mssql/server:2022-latest`), copy file `.sql` và `seed-image.sh` vào, rồi chạy
`seed-image.sh`. Kết quả của bước "tạo DB + áp schema rồi tắt" đó được Docker lưu lại thành 1 layer
**cố định trong chính image** — khác hẳn cách chạy container bình thường (mục 3), nơi database nằm
trong volume rời, tách biệt khỏi image.

```dockerfile
FROM mcr.microsoft.com/mssql/server:2022-latest
ARG SA_PASSWORD
ENV ACCEPT_EULA=Y
ENV MSSQL_SA_PASSWORD=${SA_PASSWORD}
COPY database/tripmate_schema_v7.sql /tmp/tripmate_schema_v7.sql
COPY database/seed-image.sh /tmp/seed-image.sh
RUN /tmp/seed-image.sh    # ← đây là bước "nướng" schema vào image
```

⚠️ Mật khẩu `sa` được truyền lúc build qua `--build-arg SA_PASSWORD=...` (lấy từ `.env`), không còn
hardcode trong Dockerfile. Giá trị truyền vào vẫn được "nướng" vào image (xem được bằng
`docker history`/`docker inspect`) — chỉ dùng cho **local dev** khi phân phối image cho team, tuyệt
đối không dùng cách này hay mật khẩu này cho production/cloud.

---

### 10.2 [LEADER] Bước 1 — Build image từ file `.sql`

```bash
cd Capstone_BE
# Truyền mật khẩu sa qua build-arg (lấy từ .env, không hardcode trong Dockerfile):
docker build -f database/Dockerfile.seeded -t tripmate-db:v7 --build-arg SA_PASSWORD="<SA_PASSWORD trong .env>" .
```

Theo dõi log build: sẽ thấy SQL Server khởi động, `CREATE DATABASE`, rồi log tương tự `(8 rows affected)` / `(130 rows affected)` (đúng số schema + message của `tripmate_schema_v7.sql`), cuối
cùng là dòng `Schema seeded...`. Nếu không thấy các dòng này, build đã lỗi — kiểm tra lại log phía
trên để biết bước nào fail.

### 10.3 [LEADER] Bước 2 — Xuất ra file `.tar`

```bash
docker save -o tripmate-db.tar tripmate-db:v7
ls -lh tripmate-db.tar     # thực tế: ~604 MB
```

`docker save` đóng gói toàn bộ image (SQL Server engine + database đã seed) thành 1 file di động
được. Nặng hơn nhiều so với đóng gói riêng code (chỉ vài trăm MB) vì đây là cả 1 hệ quản trị CSDL,
không chỉ vài file — nhưng 604 MB vẫn upload lên Google Drive bình thường, chỉ lâu hơn chút.

### 10.4 [LEADER] Bước 3 — Gửi file cho team

Upload `tripmate-db.tar` lên Google Drive (khuyên dùng vì dung lượng ~600 MB, Zalo dễ giới hạn),
gửi link cho team. Team vẫn cần `git pull` code repo như bình thường — file `.tar` chỉ thay cho
phần database, không thay code.

---

### 10.5 [TEAM] Bước 4 — Nạp (load) image từ file `.tar`

```bash
docker load -i tripmate-db.tar
docker images tripmate-db          # phải thấy dòng: tripmate-db   v7   2.51GB
```

Nếu không thấy dòng `tripmate-db` sau khi load → file `.tar` tải bị lỗi/thiếu, tải lại từ đầu.

### 10.6 [TEAM] Bước 5 — Tạo và chạy container từ image

```bash
docker run -d --name tripmate-sqlserver -p 14330:1433 tripmate-db:v7
```

Đây là bước "tạo container" — `docker run` tạo container mới từ image `tripmate-db:v7` và khởi
động nó ngay, map port `14330` trên máy bạn vào port `1433` bên trong container. Không cần truyền
`-e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=...` vì 2 biến đó đã nằm sẵn trong image từ lúc build. Không
cần `docker-compose.yml`, không cần chạy `apply-schema.sh` — schema đã có sẵn trong image rồi.

Kiểm tra container đã chạy:

```bash
docker ps    # thấy "tripmate-sqlserver" đang "Up"
```

### 10.7 [TEAM] Bước 6 — Xác nhận database đã sẵn sàng ngay

```bash
docker exec tripmate-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -C -S localhost -U sa -P "<SA_PASSWORD mà người build đã truyền lúc build>" -d TripMateDb \
  -Q "SELECT COUNT(*) AS tables FROM sys.tables;"
```

Kỳ vọng: `56` — ngay từ container **đầu tiên** vừa `docker run`, chưa hề chạy bất kỳ bước áp schema
nào. Từ đây, mở project trong Visual Studio/Rider (hoặc `dotnet run --project src/TripMate.Api`) và
code chạy bình thường, kết nối vào `localhost,14330` — xem mục 2-5 của [`README.md`](../README.md)
chính nếu chưa quen phần chạy code local.

---

### 10.8 Dọn dẹp

```bash
docker rm -f tripmate-sqlserver     # dừng + xoá container
docker rmi tripmate-db:v7           # xoá image
```

Lưu ý khác cách compose ở mục 3: container này **không dùng volume rời**, dữ liệu (kể cả data code
ghi vào lúc chạy thật — user đăng ký, refresh token...) nằm trong layer riêng của container đó.
Nghĩa là:

- `docker stop` rồi `docker start` lại → **giữ nguyên** data đã ghi thêm lúc chạy.
- `docker rm` rồi `docker run` lại từ **cùng image** → mất data đã ghi thêm, quay về đúng trạng thái
  seed ban đầu (56 bảng, chưa có user nào) — tiện để test lại từ đầu mà không cần lệnh reset nào.

Muốn data sống sót cả qua `docker rm` (persistence thật), mount thêm volume khi `docker run`
(`-v tripmate-db-data:/var/opt/mssql`) — nhưng khi đó mất luôn tính năng "chạy container mới là về
trạng thái sạch" ở trên, phải tự quản lý volume như cách compose đang làm ở mục 3.

### 10.9 [LEADER] Khi nào cần build và gửi lại file `.tar`

Chỉ khi `database/tripmate_schema_v7.sql` đổi (thêm bảng, đổi cột, lên `v8`...) — leader build lại
từ mục 10.2 và gửi file `.tar` mới cho team. Image này là ảnh chụp cố định tại thời điểm build,
không tự đồng bộ theo `.sql` — sửa `.sql` xong mà không build lại thì file `.tar` cũ vẫn y nguyên.
