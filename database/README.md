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

| Trường                           | Giá trị                                                                |
| ---------------------------------- | ------------------------------------------------------------------------ |
| Server / Host                      | `localhost`                                                            |
| Port                               | `1433`                                                                 |
| Authentication                     | SQL Login                                                                |
| User                               | `sa`                                                                   |
| Password                           | giá trị`SA_PASSWORD` trong `.env` (mặc định `Passw0rd!Local`) |
| Database                           | `TripMateDb`                                                           |
| Encrypt / Trust server certificate | Bật "Trust server certificate" (container dùng self-signed cert)       |

Container phải đang chạy (`docker compose ps` thấy `sqlserver` là `Up`) thì mới kết nối được.

## 8. Các lỗi thường gặp

| Triệu chứng                                                                                                | Nguyên nhân & cách xử lý                                                                                                                                                                                                                                                                                                                                                         |
| ------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `docker compose up` báo "cannot connect to the Docker daemon"                                             | Docker Desktop chưa mở. Mở app lên, đợi icon "Running" rồi chạy lại.                                                                                                                                                                                                                                                                                                         |
| `sqlserver` mãi không "Healthy", `db-init` không bao giờ chạy                                       | Cổng`1433` trên máy bạn đang bị chương trình khác (SQL Server local, container cũ...) chiếm. Đổi cổng map trong `docker-compose.yml` (`"1433:1433"` → ví dụ `"14330:1433"`) hoặc tắt SQL Server local đang chạy.                                                                                                                                        |
| `db-init` báo lỗi `Msg 1934` khi tạo index                                                            | Đang tự chạy`sqlcmd` tay mà quên cờ `-I` (xem mục 4) — `apply-schema.sh` đã tự có cờ này nên không gặp lỗi này nếu chạy qua script.                                                                                                                                                                                                                        |
| Sửa nội dung trong`tripmate_schema_v7.sql` xong chạy `docker compose up -d` mà không thấy áp lại | Nếu bảng`catalog` và `MSG130` đều đã tồn tại, script coi như "đã áp v7 rồi" và bỏ qua — sửa nội dung *bên trong* v7 không tự động được nhận diện là bản mới. Reset volume (mục 5) để áp lại từ đầu, hoặc tạo hẳn `tripmate_schema_v8.sql` mới theo đúng convention nếu đó là một thay đổi thật sự (xem `AGENTS.md`). |
| `db-init` báo lỗi "has an older schema version applied"                                                  | DB đang có bản cũ hơn v7 (ví dụ v6) từ trước. Đây là chủ đích — script từ chối chạy đè lên schema cũ để tránh lỗi "object already exists" nửa chừng. Reset volume theo mục 5 rồi chạy lại.                                                                                                                                                          |
| Trên Windows, sau khi sửa`apply-schema.sh` thì chạy báo lỗi khó hiểu trong container               | Editor lưu file với line ending CRLF thay vì LF, script Linux không chạy được. Repo đã có`.gitattributes` ép LF cho `.sh`/`.sql` — đảm bảo bạn không tắt/bypass nó, và nếu tự tạo file `.sh` mới nhớ lưu LF.                                                                                                                                       |
| Muốn xem log chi tiết một service                                                                         | `docker compose logs -f <tên-service>` (ví dụ `docker compose logs -f sqlserver`, `docker compose logs -f api`).                                                                                                                                                                                                                                                             |
| Muốn dừng hẳn mà giữ data để hôm sau chạy tiếp                                                     | `docker compose stop` (khác với `down -v` — không xoá container lẫn volume).                                                                                                                                                                                                                                                                                                |

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
