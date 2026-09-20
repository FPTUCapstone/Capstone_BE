# TM-70 / UC-24 — Search Tours: đặc tả chức năng và tích hợp

**Phiên bản:** 1.2 — 19/09/2026. **Trạng thái:** owner đã duyệt D1 nhiều vùng và triển khai Database/Backend ngày 19/09/2026; D0, D2–D8 giữ nguyên. Web/Mobile UI và tích hợp client được hoãn sang Phase 2.

**Task owner:** Mai Nguyễn Tiến Đạt / datmnt. **Nền tảng:** Backend + responsive Web + Flutter Mobile.

**Quyết định của người dùng:** chỉ ba bộ lọc Jira: **điểm đến, ngày đi, mức giá**. Phần SRS mở rộng tách đợt sau. Ngày 19/09/2026, owner làm rõ: một tour phải tìm được theo **tất cả vùng Tour Operator đã chọn là nơi tour đi qua**, kể cả tour Đà Nẵng → Hội An khi tìm Hội An. Owner chọn Tour Operator gắn vùng, không tự suy ra bằng GPS/POI. Owner đã duyệt cấu trúc D1 nhiều vùng để triển khai Backend/DB.

[Implementation plan](../plans/TM-70-plan.md) mô tả thứ tự thực hiện, file dự kiến, test và các cổng kiểm tra.

## 1. Căn cứ và độ tin cậy

| Nguồn | Điều đã xác minh | Cách sử dụng |
| --- | --- | --- |
| [Jira TM-70](https://tripmate-capstone.atlassian.net/browse/TM-70) | UC-24 Search Tours; Guest/Traveler; điểm đến, ngày đi, mức giá; assignee là task owner; lúc kiểm tra 15/09 đang To Do | Phạm vi task; không tự đổi status |
| Người dùng, quyết định trong phiên làm việc này | Ba bộ lọc Jira trước; SRS mở rộng sau | Quyết định phạm vi cao nhất cho đợt này |
| SRS workspace `D:\CapStone\report\Report3_Software-Requirement-Specification.docx`, §3.5.1, trang 115–118 | BR-52–BR-56; submit search, public visibility, current availability, giữ list khi lỗi, không tạo booking | Giữ quy tắc chung; không nhận toàn bộ bộ lọc từ hình minh họa |
| SRS §5.2, trang 275–276 | CR-01–CR-15: mặc định 20/trang, validation hai phía, UTC, VND nguyên, UI English, lỗi không báo thành công giả | Ràng buộc hợp đồng và UI |
| SRS §5.3, trang 278/280/283 | MSG29 thực tế nói về tọa độ POI; MSG64/65/127 áp dụng tour/khả dụng/lỗi chung | Không tái sử dụng MSG29 sai ngữ nghĩa |
| Workspace `D:\CapStone\Capstone_Docs\requirements\mvp\search-tours-mvp.md`, `ux\screen-specifications\search-tours-screen-spec.md`, `ux\user-flows\search-tours-user-flow.md` | Bản cũ rộng hơn Jira; chưa đủ quyết định thực thi | Phải đồng bộ sau khi duyệt; không coi là spec TM-70 đã chốt |
| Workspace `D:\CapStone\Capstone_Docs\api\contracts\api-response-standard.md` | DTO trực tiếp; lỗi ProblemDetails/ValidationProblemDetails; camelCase; nullable giữ key | Không dùng envelope success/data |
| [Web scope matrix](https://github.com/FPTUCapstone/Capstone_FE/blob/develop/docs/WEB_SCOPE_MATRIX.md) | UC-24 thuộc SHARED_WEB_MOBILE | Hai client dùng cùng contract |
| Workspace `D:\CapStone\TEAM_ENGINEERING_RULES.docx`, `D:\CapStone\Dev_and_CrossReview_Checklist.pdf` v2.0 | Spec/plan approval, SQL thật, full build, evidence theo head, bảo toàn worktree | Cổng kiểm tra trong plan |

SRS được đọc cả text và ảnh các trang liên quan, không chỉ đọc hình/mockup. SHA-256 của DOCX lúc đối chiếu: EF675A01A1F459C1A0994AB0F40B0C99BBAF55CCD68A0F882787D37A12C681B3.

### 1.1 Baseline develop đã fetch để lập kế hoạch

| Repo | origin/develop tại lần kiểm tra | Phát hiện ảnh hưởng thiết kế |
| --- | --- | --- |
| Capstone_BE | 86516e2a85394341e6fad45bc78e3899c363ead6 | Có bảng Tours/TourSchedules trong SQL; chưa có public Tour Search và EF entities tương ứng. Đây là base Phase 1 sau khi fetch lại |
| Capstone_FE | 95bde9ea7c098039770f74ecb878f5b9a29c1756 | Next.js App Router; chưa có public /tours; có Vitest; build phụ thuộc cấu hình Firebase của các trang hiện hữu |
| Capstone_Mobile | 833e7d8bd8389f7b926aa11b6966974f4c211116 | Flutter + Bloc/Cubit, Dio, go_router; chưa có Tour Search |

Các SHA là bằng chứng tại thời điểm lập tài liệu, không khóa develop cho lần triển khai sau. Phải fetch/re-audit trước khi tạo nhánh.

Baseline Backend Phase 1 tại 86516e2 đã chạy ngày 15/09/2026: Release build 0 warning/0 error; 170 Application + 3 Infrastructure + 61 API integration = 234 pass, trong đó SQL-enabled integration suite 61/61 pass, 0 skip trên container test riêng. Chưa chạy baseline FE/Mobile vì được hoãn sang Phase 2; chưa kiểm tra hoặc sửa nội dung TripMateDb của người dùng. Không lấy số test từ PR TM-58/TM-98 làm bằng chứng TM-70.

## 2. Mục tiêu, phạm vi và điều kiện hoàn thành

Guest hoặc Traveler mở Tours, nhập tiêu chí tùy chọn, bấm Search, xem đúng các tour công khai phù hợp cùng giá và tình trạng lịch/chỗ lấy từ SQL Server. Không phải đăng nhập để tìm kiếm.

### 2.1 Trong phạm vi

1. Public read-only API GET /api/v1/tours.
2. Điểm đến dạng văn bản; một ngày khởi hành; khoảng giá tối thiểu/tối đa.
3. Kết hợp bộ lọc bằng AND; giá min/max thuộc cùng một bộ lọc.
4. Mặc định 20 kết quả/trang; total count; thứ tự ổn định; giữ tiêu chí/trang khi quay lại.
5. Web /tours và Mobile /tours, Guest entrypoint và Traveler entrypoint.
6. Loading, empty, validation, network/server error, retry, stale response, thay đổi khả dụng.
7. Hợp đồng tích hợp với UC-26/TM-72; kiểm chứng hành vi quay lại danh sách.
8. Migration/full schema phục vụ dữ liệu tìm kiếm sau khi được duyệt, cùng SQL integration tests.

### 2.2 Ngoài phạm vi

- Keyword tìm theo tên tour/operator; date range; duration/category/rating/interest filters; user-selectable sort.
- Recommendation UC-25; lưu TourSearchRequests; booking/giữ chỗ/thanh toán; favorites.
- Google Maps, tọa độ/GPS, map preview; không bê yêu cầu map của TM-58 vào task này.
- Tạo/sửa tour, publication workflow, quản lý ảnh, xây màn hình chi tiết UC-26.
- Ảnh tour, rating, giảm giá, giá gạch ngang, phần trăm khuyến mại khi chưa có nguồn dữ liệu/contract được duyệt.
- Đổi framework/auth provider, sửa layout toàn app, dọn các nhánh review/UC-52, merge các PR đang review.

**Không kết luận “hoàn thành toàn bộ UC-24 SRS”.** Đầu ra là TM-70 phase 1 theo phạm vi đã chốt. Các tính năng SRS bị hoãn phải được liệt kê trong mô tả PR.

## 3. Sổ quyết định và cổng duyệt

Các quyết định dưới đây là baseline đã được owner duyệt cho Phase 1. Muốn thay đổi phải cập nhật spec và được duyệt lại trước khi sửa code.

| ID | Đề xuất thực thi | Trạng thái / lý do cần chốt |
| --- | --- | --- |
| D0 | Chỉ điểm đến + ngày đi + mức giá; phần rộng hơn tách sau | **ĐÃ DUYỆT** |
| D1 (sửa đổi) | Danh mục `catalog.Destinations` do TripMate quản lý; Tour Operator gắn mọi vùng tour đi qua vào `commerce.TourDestinations` theo thứ tự hành trình. GET tìm literal trên tên của **bất kỳ** vùng đã gắn; mỗi tour chỉ xuất hiện một lần. Không suy ra vùng từ tên tour, meeting point, địa chỉ POI hay GPS. | **ĐÃ DUYỆT 19/09/2026**. Thay thế phương án một chuỗi `Tours.destination` trước đây. |
| D2 | Một departureDate theo lịch Asia/Ho_Chi_Minh; so với ngày bắt đầu của lịch Scheduled chưa khởi hành; không phải ngày tour đi qua điểm đến | **ĐÃ DUYỆT** |
| D3 | Giá lọc là base_price, VND nguyên, biên inclusive; SQL giữ DECIMAL(12,2) nhưng thêm whole-dong constraint sau audit; không làm tròn dữ liệu cũ | **ĐÃ DUYỆT** |
| D4 | Tour Approved + published_at <= hiện tại và khác null; operator Approved, user operator Active; tour hết chỗ vẫn được browse nếu phù hợp bộ lọc | **ĐÃ DUYỆT**; discovery tách booking |
| D5 | Đại diện lịch: lịch tương lai phù hợp sớm nhất còn chỗ; nếu tất cả full thì lịch sớm nhất; số chỗ là capacity của lịch đó, không cộng nhiều lịch | **ĐÃ DUYỆT**, nhưng Phase 1 phải xác minh reserved_capacity là counter hợp lệ trước khi tuyên bố availability hoàn chỉnh |
| D6 | Mặc định title tăng dần rồi tour_id; Web numbered pagination, Mobile Previous/Next + số trang, 20/trang; không sort control | **ĐÃ DUYỆT**; Phase 1 áp dụng API ordering/paging, UI để Phase 2 |
| D7 | Theo CR-09: UI English, resource files; placeholder thật khi thiếu ảnh; feature-local validation messages, không dùng MSG29 cho giá/ngày | **ĐÃ DUYỆT**; Phase 1 áp dụng error contract, UI để Phase 2 |
| D8 | TM-72 cung cấp detail route thật; trước đó chỉ test adapter/route harness, chưa nghiệm thu end-to-end card → detail → back | **ĐÃ DUYỆT**, thực hiện trong Phase 2 |

**G0 ban đầu: ĐẠT ngày 15/09/2026; D1 sửa đổi được owner duyệt 19/09/2026.** **G1:** duyệt screen spec Web/Mobile và mapping frame đúng UC-24, được hoãn sang Phase 2. Không tự khẳng định “đúng Figma” khi G1 chưa đạt.

## 4. Dữ liệu và quy tắc nghiệp vụ đề xuất

### 4.1 Nguồn sự thật

| Dữ liệu | Nguồn SQL | Quy tắc |
| --- | --- | --- |
| Identity, title, basePrice, durationDays | commerce.Tours | Chỉ project field public; không trả description đầy đủ trong list |
| Destinations | `catalog.Destinations` + `commerce.TourDestinations` | Các vùng đã được Tour Operator chọn cho tour, theo thứ tự; không tự tách title/meeting_point/POI address |
| Publication | Tours.status, published_at | Đủ điều kiện đồng thời; không chỉ kiểm tra Approved |
| Tên operator, approval | dbo.OperatorProfiles, dbo.Users | Chỉ public companyName; không email, phone, hồ sơ duyệt, reviewer ID |
| Lịch và capacity | commerce.TourSchedules | status, start/end, total_capacity, reserved_capacity |
| Ảnh/rating | Chưa có nguồn đủ điều kiện cho card TM-70 | Không lấy POIPhotos làm ảnh tour, không dùng review không có quy tắc publication |

TourSearchRequests yêu cầu Traveler và phục vụ luồng khác. GET này không ghi bảng đó, không gọi SaveChangesAsync, không tạo Booking, không sửa Tour/TourSchedule.

### 4.2 Điểm đến

- `catalog.Destinations`: `destination_id BIGINT IDENTITY` và `name NVARCHAR(300) COLLATE Vietnamese_100_CI_AS NOT NULL UNIQUE`. Danh mục do TripMate quản lý và bổ sung theo vùng tour thực sự phục vụ; không tự seed tên địa phương giả hoặc coi Google Places là nguồn nghiệp vụ.
- `commerce.TourDestinations`: `(tour_id, destination_id)` duy nhất, FK đến Tour/Destination và `sequence_no INT > 0` duy nhất theo tour để hiển thị thứ tự vùng trong hành trình. Tour Operator gắn **tất cả vùng tour đi qua** khi tạo/sửa tour (UC-35); trách nhiệm giữ nhãn vùng đúng khi đổi lịch trình thuộc writer UC-35.
- Ví dụ tour Đà Nẵng → Hội An được gắn `Đà Nẵng` và `Hội An`: tìm từng tên đều trả cùng tour **một lần** trong items/count. Tour quanh Hội An chỉ xuất hiện nếu Tour Operator xác nhận và gắn vùng Hội An. “Quanh vùng” trong phase này là vùng được gắn, không phải bán kính GPS hoặc tự phân tích tọa độ POI.
- Sau trim, chuỗi trắng được coi là không lọc. Chuẩn hóa Unicode NFC ở server và client; dài tối đa 300 UTF-16 code units. Không cắt bớt âm thầm. Query text được tìm như **chuỗi literal** trên tên vùng đã gắn; %, _, [, ], dấu nháy và ký tự escape không được mở rộng tập kết quả. Query parameterized, dùng `EXISTS`/`Any` để không nhân đôi tour.
- Không phân biệt hoa/thường; **có phân biệt dấu** theo Vietnamese_100_CI_AS. Không hứa “Da Nang” khớp “Đà Nẵng”; hỗ trợ alias/không dấu hoặc tìm bán kính cần quyết định riêng.
- Tour cũ chưa có vùng: vẫn hiện khi không lọc, response `destinations: []`; khi lọc vùng thì không match. Data owner rà soát và gắn từng tour theo bảng `tour_id → destination_id(s)`; không tự đoán từ title, meeting point, địa chỉ POI hoặc tọa độ. Trước khi nghiệm thu filter với dữ liệu thật phải backfill các tour public liên quan.
- TM-70 là **đọc**; không thêm thao tác quản trị danh mục hoặc API tạo/sửa Tour vào task này. UC-35 phải bổ sung writer chọn nhiều vùng từ danh mục; nguồn và quyền duyệt danh mục do data owner cung cấp trước khi tích hợp UI. Bản đồ, GPS và thông báo sắp đến POI là luồng khác.
- **Phân biệt vùng tìm tour với khám phá POI:** `TourDestinations` chỉ quyết định tour có khớp bộ lọc điểm đến TM-70; nó không quyết định POI nào khách được nhìn thấy. `TourItineraryItems` là các điểm dừng thuộc lịch trình chính thức do Tour Operator chọn. POI `Active` trong `catalog.POIs` có thể được khám phá độc lập theo tọa độ hiện tại của người dùng, kể cả khi không có trong lịch trình/tour không được gắn POI; khi hiển thị phải ghi rõ là **gợi ý gần bạn, ngoài lịch trình**, không ngụ ý tour sẽ ghé hoặc bao gồm chi phí/thời gian tham quan. POI phải tồn tại trong danh mục trước; việc người dùng đi gần không tự tạo bản ghi POI.
- Gợi ý theo **vị trí hiện tại** khác với tìm mọi POI **dọc toàn bộ tuyến đường** (cần hình học tuyến và quy tắc khoảng cách), và càng khác với **thông báo tự động khi sắp đến** (cần thiết kế quyền vị trí, ngưỡng, chống lặp, vòng đời ứng dụng). Hai chức năng sau chưa thuộc TM-70 Phase 1; không suy ra đã triển khai từ bộ lọc điểm đến hay từ dữ liệu POI có tọa độ.

### 4.3 Ngày đi và múi giờ

- departureDate là DateOnly ISO yyyy-MM-dd, không có giờ/offset; UI hiển thị dd/MM/yyyy.
- Chụp một asOfUtc qua TimeProvider cho mỗi lần thực thi query nhất quán; mọi phép so sánh trong response dùng giá trị này.
- Chuyển 00:00 ngày người dùng chọn tại Asia/Ho_Chi_Minh sang UTC; upper bound bằng startUtc + một ngày. Predicate **start_datetime >= startUtc AND start_datetime < endUtc AND start_datetime > asOfUtc**.
- Dùng khoảng nửa mở, không lấy .Date theo timezone máy chủ hoặc tính 23:59:59.999. Không so với end_datetime.
- Chỉ xét lịch Scheduled, end_datetime > start_datetime. Cancelled/Completed và lịch đã bắt đầu không đại diện lịch tương lai.
- Không nhập ngày: lấy các lịch Scheduled tương lai của tour. Nhập ngày quá khứ hợp lệ: 200 empty, không tự đổi sang hôm nay.
- Ngày không tồn tại, timestamp thay vì date, hoặc giá trị không thể chuyển thành khoảng UTC hợp lệ: 400 theo field departureDate; không 500 do overflow.
- Ví dụ ngày 20/09/2026 tương ứng [2026-09-19T17:00:00Z, 2026-09-20T17:00:00Z). Test đúng cả hai biên.
- CR-07 yêu cầu lưu UTC nhưng DATETIME2 không mang timezone. Trước release phải xác minh convention của dữ liệu lịch cũ và writer UC-35. Không tự trừ 7 giờ trên toàn bảng.

### 4.4 Giá

- minPrice/maxPrice đều tùy chọn; thiếu một đầu là không giới hạn phía đó. 0 hợp lệ, không bị bỏ do truthy/falsy.
- Đơn vị VND nguyên, từ 0 đến **9,999,999,999** (giới hạn nguyên nằm trong DECIMAL(12,2)).
- Transport query chỉ chữ số thập phân không dấu nhóm, không exponent, không dấu âm/dấu cộng, không phần lẻ; UI có thể nhóm hàng nghìn nhưng serialize về chữ số.
- minPrice <= maxPrice. Equal bounds hợp lệ. Cả hai biên inclusive trên Tours.base_price.
- Không nhân số khách, không cộng phí/thuế, không chọn giá lịch, không áp voucher. UI ghi “Base price”; không tự gọi “total payable” hoặc “per person” khi chưa có hợp đồng đơn vị tính.
- Chưa đổi kiểu vật lý DECIMAL để tránh breaking migration. Đề xuất CHECK base_price = FLOOR(base_price), giữ constraint >= 0 hiện có.
- Nếu DB hiện có giá lẻ: dừng migration với thông báo an toàn, xuất danh sách ID cần owner xử lý trong quy trình bảo mật; không ROUND/TRUNCATE, không bỏ tour âm thầm, không tuyên bố đáp ứng CR-08.

### 4.5 Visibility và availability

Predicate D4 áp dụng cho cả items và totalCount: Tours.status = Approved; published_at khác null và <= asOfUtc; OperatorProfiles.approval_status = Approved; tài khoản operator có AccountStatus.Active. Không dựa vào việc UI ẩn nút.

Không có thêm endpoint nhận arbitrary status/operator filter. Tour Pending/Draft/Rejected/Inactive, publish tương lai hoặc thiếu operator hợp lệ không lộ qua list/count.

Tập lịch C: lịch Scheduled chưa bắt đầu của tour; nếu có departureDate thì thêm khoảng ngày ở §4.3. Mỗi tour chỉ xuất hiện **một lần**:

1. Nếu có ít nhất một lịch trong C còn chỗ, chọn lịch còn chỗ sớm nhất theo start_datetime rồi schedule_id.
2. Nếu C có lịch nhưng tất cả full, chọn lịch sớm nhất theo cùng thứ tự.
3. Nếu C rỗng và không lọc ngày, vẫn cho browse tour công khai với noUpcomingSchedule.
4. Nếu C rỗng và có lọc ngày, loại tour khỏi cả items và count.

| availabilityStatus | representativeScheduleId / departureAtUtc | remainingSlots | UI |
| --- | --- | --- | --- |
| available | ID / thời điểm lịch đã chọn | total_capacity - reserved_capacity > 0 | “N seats left for this departure” |
| soldOut | ID / thời điểm lịch đã chọn | 0 | “Sold out for this departure”; không giả thành tour chưa publish |
| noUpcomingSchedule | null / null | null | “No upcoming departures” |
| unknown | null / null | null | “Availability unavailable”; không hiện 0 hoặc “available” |

- Không SUM capacity các lịch khác ngày; không trừ Booking một lần nữa khỏi reserved_capacity.
- Counter chỉ đáng tin khi writer giữ/nhả chỗ cập nhật đúng. Xác nhận hợp đồng counter với task booking; thiếu bằng chứng thì không nghiệm thu “real availability”.
- unknown dành cho trường hợp thông tin riêng tour có lỗi toàn vẹn/không thể xác định được một cách tách biệt, vẫn có dữ liệu public identity hợp lệ. Log mã lỗi nội bộ, không trả lỗi DB.
- Nếu không xác minh được predicate ngày/publication hoặc query toàn bộ thất bại, trả lỗi request; không biến thành 200 unknown hàng loạt để che sự cố DB.
- Số chỗ là snapshot tại thời điểm response, **không phải reservation hoặc cam kết booking thành công**. UC-26/booking phải đọc lại.
- BR-60 của recommendation không được dùng để âm thầm loại mọi tour sold out khỏi browse UC-24; chính sách này được nêu trong D4 để duyệt.

### 4.6 Phân trang và tính nhất quán

- page mặc định 1; pageSize mặc định 20, API cho phép 1–100; UI phase 1 luôn dùng 20.
- page là số nguyên dương; kiểm tra cả tràn số và offset = (page - 1) * pageSize trước khi gọi Skip/SQL. Offset không vượt Int32.MaxValue.
- Thứ tự title ascending theo collation D1, rồi tour_id ascending làm tie-breaker. Không dùng thứ tự mặc định của DB.
- Count đếm tour phù hợp, không đếm số schedule. totalPages = ceiling(totalCount/pageSize); không có tour thì totalPages = 0, items = [].
- page vượt totalPages: 200 items rỗng nhưng giữ count thật và page được yêu cầu; không trả 404/500. UI giải thích trang không còn dữ liệu và cho “Back to first page”, không tự lặp request vô hạn.
- Query count + page phải đọc nhất quán trong **một transaction read-only ngắn** ở mức Serializable theo cơ chế repo hiện có, cùng asOfUtc; không giữ transaction qua serialize/network. Bắt buộc đo contention trong plan; nếu đổi chiến lược cần ADR và test tương đương, không hạ isolation âm thầm.
- Hai lần request khác nhau có thể có count/thứ tự thay đổi do publication/capacity mới. Không hứa snapshot bất biến xuyên nhiều trang.

## 5. Hợp đồng API dự kiến

### 5.1 Request

GET /api/v1/tours — AllowAnonymous; không yêu cầu access token, idempotency key hay body.

| Query | Kiểu | Default | Validation |
| --- | --- | --- | --- |
| destination | string optional | absent | §4.2 |
| departureDate | ISO date optional | absent | §4.3 |
| minPrice | integer VND optional | absent | §4.4 |
| maxPrice | integer VND optional | absent | §4.4 và >= minPrice |
| page | integer | 1 | > 0, offset không overflow |
| pageSize | integer | 20 | 1–100 |

Optional field rỗng/whitespace là absent. page/pageSize nếu hiện diện nhưng rỗng là lỗi, không rơi về default. Reject duplicate supported query keys (kể cả khác casing) và query key không hỗ trợ bằng 400; không silently ignore sort/rating rồi khiến UI tưởng filter hoạt động.

Client gửi camelCase và canonical encoding. Binding failures (abc cho page, ngày sai) cũng phải có field-level errors camelCase như validator; không chỉ test lỗi FluentValidation.

Ví dụ:

~~~http
GET /api/v1/tours?destination=%C4%90%C3%A0%20N%E1%BA%B5ng&departureDate=2026-09-20&minPrice=500000&maxPrice=1500000&page=1&pageSize=20
~~~

### 5.2 Success 200

~~~json
{
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1,
  "asOfUtc": "2026-09-15T03:00:00Z",
  "items": [
    {
      "tourId": "101",
      "title": "Da Nang discovery tour",
      "destinations": ["Đà Nẵng", "Hội An"],
      "operatorName": "Example Travel",
      "durationDays": 1,
      "basePrice": 1190000,
      "currency": "VND",
      "representativeScheduleId": "501",
      "departureAtUtc": "2026-09-20T01:00:00Z",
      "availabilityStatus": "available",
      "remainingSlots": 12
    }
  ]
}
~~~

**Ví dụ tổng hợp cho contract, không phải dữ liệu đang có trong DB.**

| Field | Kiểu/nullability | Invariant |
| --- | --- | --- |
| tourId | string, required | Chuỗi số nguyên dương decimal của SQL BIGINT; không ép Number/JS hoặc Dart Web double |
| title | string, required | Tối đa 200 UTF-16 units; render text, không HTML |
| destinations | array string, required | Tên các vùng đã gắn theo `sequence_no`; `[]` khi chưa được gắn; không nhân đôi tên |
| operatorName | string, required | Tên công ty public; không profile nội bộ |
| durationDays | integer, required | > 0, thông tin card; không thêm duration filter |
| basePrice | JSON integer number | VND nguyên trong giới hạn §4.4, an toàn JS integer |
| currency | string | Luôn VND; không giả hỗ trợ đa tiền tệ |
| representativeScheduleId | string hoặc null | BIGINT dạng chuỗi; semantics §4.5 |
| departureAtUtc | RFC3339 UTC string hoặc null | Có Z; UI convert theo timezone cố định, không timezone thiết bị |
| availabilityStatus | enum string | available, soldOut, noUpcomingSchedule, unknown |
| remainingSlots | integer >= 0 hoặc null | Theo bảng §4.5, không normalize null thành 0 |
| asOfUtc | RFC3339 UTC string | Mức page, một giá trị cho toàn response |
| totalCount / totalPages | integer number | Non-negative, <= JS safe integer; checked arithmetic |

Không có success/data wrapper, thumbnailUrl, rating, reviewCount, discount, tags, canBook hay thông tin thanh toán. Schema runtime phải kiểm tra presence, type, nullability và enum; không cast JSON rồi tin tưởng.

### 5.3 Lỗi, header và tính riêng tư

~~~json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "maxPrice": ["Maximum price must be greater than or equal to minimum price."]
  }
}
~~~

| Kết quả | HTTP / body | UI/client |
| --- | --- | --- |
| Đúng request, không tour | 200 page DTO rỗng | MSG64, giữ criteria, Clear filters |
| Query invalid | 400 ValidationProblemDetails | Inline theo errors; unknown field ở form summary; giữ list thành công trước |
| Lỗi hệ thống BE | 500 generic ProblemDetails | MSG127 + Retry; không trace SQL/stack/token |
| Hạ tầng có giới hạn request | 429 nếu được cấu hình | Tôn trọng Retry-After nếu có; không retry storm; không khẳng định hiện đã có limiter |
| FE upstream không kết nối / timeout | BFF 503 / 504 ProblemDetails | Lỗi, không list rỗng giả |
| FE upstream trả HTML, JSON sai shape hoặc status bất thường | BFF 502 ProblemDetails | Contract/upstream failure; không map 200 thành success UI |

Không bắt buộc errors phải có detail/type/errorCode nếu runtime không trả. Business errors tương lai phải có stable errorCode theo API standard, không parse English title để quyết định logic.

Mọi response endpoint và BFF dùng Cache-Control: no-store. Không ISR/use cache/Redis/result cache cho availability. Client lưu criteria, không coi list cache từ phiên trước là dữ liệu mới.

Public client không cần gửi bearer/cookie và không cần refresh token để tìm kiếm. Backend phải kiểm tra guest, token hợp lệ và token hỏng/hết hạn trên endpoint AllowAnonymous; một optional token không được biến public search thành protected flow. Không đổi chính sách route bảo vệ khác.

Không trả operator_user_id, reviewer_user_id, booking data, email, phone, SQL query, connection string. Log route, duration, status, correlation ID và mã failure; không log nguyên token hoặc toàn bộ input tự do.

## 6. Luồng và trạng thái client

### 6.1 Mô hình state chung

- draftCriteria: dữ liệu đang sửa trong form.
- requestedCriteria: tiêu chí của request đang chạy hoặc lần lỗi để Retry.
- displayedCriteria + pageData: cặp tiêu chí/kết quả của lần thành công gần nhất; không đổi nhãn list sang tiêu chí mới khi request mới thất bại.
- requestVersion/cancel token: chỉ response của request hiện hành được phép cập nhật UI; dispose/unmount không emit.
- freshness: fresh / revalidating / stale; criteria có thể lưu lại, số chỗ stale không được trình bày như live.

### 6.2 Bảng chuyển trạng thái

| Sự kiện | Request / cập nhật | Điều người dùng thấy |
| --- | --- | --- |
| Mở /tours lần đầu | Một GET tiêu chí mặc định hoặc URL hợp lệ | Form + skeleton, rồi list/empty/error |
| Gõ/chọn/chỉnh field | Chỉ cập nhật draft | Không API mỗi phím, không đổi list/count |
| Search, form sai | Không request | Inline error, focus field đầu tiên, giữ list |
| Search, form đúng | Chuẩn hóa; reset page 1; hủy request cũ | Progress; giữ identity list cũ nhưng che/đánh dấu số chỗ chưa mới |
| Request thành công | Cập nhật đồng thời displayedCriteria và pageData | List/count/page đúng cùng response; công bố kết quả cho screen reader |
| 400 từ server | Giữ draft, list và displayedCriteria cũ | Inline + summary; không hiển thị empty |
| Network/500 sau lần thành công | Giữ list/criteria đã hiển thị, freshness stale | MSG127; “Results could not be refreshed”; không quảng bá số chỗ cũ |
| Lỗi lần đầu | Không có list để giữ | Full error state + Retry |
| Retry | Gửi lại đúng requestedCriteria, không lấy draft chưa submit | Không mất input mới đang gõ; response vẫn được gắn đúng tiêu chí |
| Clear filters | Reset tất cả field + page 1 rồi GET đúng một lần | Trở lại mặc định; không còn keyword vì phase này không có keyword |
| Đổi trang | Dùng displayedCriteria; nếu form có draft chưa apply, chỉ rõ “Changes not applied” | Không âm thầm áp draft |
| Response A tới sau B | Bỏ A dù server chưa nhận cancel | Không rollback list/count/page của B |
| Detail → Back / app resume | Khôi phục tiêu chí/trang/scroll; GET revalidate | Không hiển thị capacity cũ như mới; restore scroll sau layout ổn định |
| Mất mạng offline | Không ghi pending search/booking | Cho sửa form; lần search báo lỗi; chỉ giữ list cũ có stale warning |

Double click Search cùng tiêu chí khi pending: chặn/disable submit tương ứng; đọc GET không cần bảng idempotency. Tiêu chí mới vẫn có thể thay request cũ sau khi user submit.

### 6.3 URL và navigation

- Web: /tours?destination=...&departureDate=...&minPrice=...&maxPrice=...&page=2; pageSize 20 mặc định không cần hiện URL.
- URL biểu diễn tiêu chí được yêu cầu; nếu request lỗi, list cũ được gắn nhãn displayedCriteria riêng. Back/forward khôi phục form và refetch; tránh vòng lặp URL → effect → URL.
- Chuẩn hóa URL không làm mất field unknown im lặng: báo query unsupported rồi cho reset. Không nhận URL upstream từ query.
- Mobile giữ state khi push detail rồi pop; public /tours nằm ngoài các prefix yêu cầu login. Không đổi toàn bộ cấu trúc tab chỉ để giống mockup.
- Guest entrypoint: Web Tours link/landing CTA; Mobile nút “Browse tours” ở entry/splash/login và public route. Traveler dùng entry trong shell phù hợp navigation hiện hữu; cấu trúc cuối phải qua G1.
- Khi UC-26 sẵn sàng: adapter truyền tourId dạng chuỗi, giữ return location nội bộ đã allowlist. Không gửi schedule ID giả hoặc biến lựa chọn card thành booking.
- Nếu UC-26 chưa có: không tạo trang chi tiết fake, không dùng route trả 404 để tuyên bố handoff hoàn tất. Có thể review module search riêng, nhưng end-to-end gate còn pending.

## 7. Đặc tả UI và responsive để duyệt

Chưa xác minh Figma node cụ thể cho UC-24 trong lượt này. Các node POI 149-1003/149-1977 của TM-58 **không phải bằng chứng thiết kế Tour Search**. Hình SRS trang 116 chỉ là tham khảo cấu trúc, có nhiều trường ngoài phase 1.

### 7.1 Cấu trúc Web

1. Public navigation hiện có + Tours active; không sidebar Admin.
2. H1 “Find tours”, mô tả ngắn về tìm tour công khai.
3. Form có Destination, Departure date, Minimum price (VND), Maximum price (VND), Search, Clear filters.
4. Applied criteria summary + totalCount; không search-as-you-type, không fake sort dropdown.
5. Result list/card: title, danh sách destinations, operatorName, durationDays, basePrice, ngày lịch đại diện, availability theo §4.5.
6. Pagination dưới list; “Showing A–B of N tours”, empty hiển thị “0 tours”, không “1–0”.
7. Vùng error/status có thể truy cập bàn phím, không chỉ toast.

Desktop >= 1024px: form sidebar 280–320px + vùng kết quả minmax(0,1fr), hoặc form trên cùng nếu G1 chọn; chiều rộng content tối đa đề xuất 1280px. Không map column.

Tablet 768–1023px: form trên, tối đa 2 cột field; card không bị bó bởi sidebar.

Web < 768px: một cột, padding ngang 16px (12px nếu thật cần tại 320), form/filters disclosure truy cập được; không ép bốn field một hàng. Min/max xếp dọc nếu label/input không đủ chỗ.

### 7.2 Cấu trúc Mobile

App bar “Tours”; form hoặc filter sheet; submitted criteria summary; list một cột; pagination compact; safe area và navigation phù hợp Guest/Traveler.

- Card ưu tiên text và thông tin thực; không bắt buộc ảnh hero khi API chưa có ảnh. Placeholder phi ảnh chỉ khi thiết kế duyệt, nhãn/alt không giả có photo tour.
- Title wrap tối đa 2–3 dòng theo mockup duyệt; không ngắt từng ký tự, có truy cập tên đầy đủ khi cần. Không height cố định khiến font lớn bị cắt.
- Badge availability có icon + text, không chỉ màu; null không biến thành số 0.
- Filter sheet cuộn được với keyboard mở; action không nằm dưới bàn phím/bottom nav; back dismiss sheet trước khi rời màn hình.
- Page controls có target tối thiểu 48dp; text scale 1.0/1.3/2.0; SafeArea; không tạo bottom bar chồng bar hiện có.

### 7.3 Text/format/accessibility

- UI English theo CR-09, đặt trong resource của feature; code không hardcode thông báo rải rác. Ngôn ngữ spec này là tiếng Việt để team review, không phải yêu cầu UI Việt.
- Date dd/MM/yyyy, time HH:mm theo Asia/Ho_Chi_Minh trên cả Android, trình duyệt và máy chủ.
- VND dùng hàng nghìn, không số lẻ; ví dụ 1,190,000 VND. Filter không dùng floating arithmetic.
- Label luôn hiện, không dùng placeholder thay label. Form có tên accessible; error liên kết field; keyboard Enter submit.
- Tab order logic; focus visible; focus trap/return focus của sheet; announce count/error vừa đủ, không announce mỗi phím; contrast được đo ở G1.
- Kiểm tra width 320/360/390/430/768/1024/1440, landscape và zoom 200%; không horizontal body overflow. Horizontal chip scroll không được che control.
- Skeleton không chứa số chỗ/giá giả; giảm animation theo reduced motion.

### 7.4 Text lỗi

| Resource / nguồn | English text hoặc quy tắc |
| --- | --- |
| MSG64 | No tour packages found matching your destination and dates. |
| MSG127 | TripMate is temporarily unable to process your request. Please check your connection and try again. |
| tourSearch.destinationTooLong | Destination must not exceed 300 characters. |
| tourSearch.invalidDepartureDate | Enter a valid departure date. |
| tourSearch.invalidPrice | Enter a whole VND amount between 0 and 9,999,999,999. |
| tourSearch.invalidPriceRange | Maximum price must be greater than or equal to minimum price. |
| tourSearch.staleResults | Results could not be refreshed. Availability may have changed. |
| tourSearch.invalidPage | This results page is not available. Return to the first page. |

Các key tourSearch.* là đề xuất mới của feature, không tự nhận là MSG global đã được duyệt. Không dùng MSG29 tọa độ cho lỗi giá/ngày. MSG65 dành thông điệp unavailable-for-booking khi đúng ngữ cảnh; không dùng nó để báo lỗi mạng.

## 8. Kiến trúc, an toàn SQL và giới hạn kỹ thuật

### 8.1 Backend

- Controller mỏng → CQRS query/validator/handler → IApplicationDbContext; Domain entities độc lập Infrastructure; mapping explicit đúng schema, enums string, lengths, nullable và UTC.
- Dùng Result<T> trong Application; controller trả Ok(dto), lỗi đúng pipeline; không gọi helper Success tạo envelope cũ.
- Bổ sung DbSets vào cả interface, implementation và test doubles; không để source compile còn test project fail do thiếu member.
- AsNoTracking + projection chỉ field cần thiết; query filter trước pagination; không ToList toàn catalogue, không Include toàn bộ itinerary/photo/review.
- Thực hiện count + items trong transaction read-only ngắn §4.6; không N+1 theo tour. CancellationToken xuyên xuống EF/SQL; không nuốt cancellation thành “empty”.
- Chỉ tạo entity/mapping tối thiểu phục vụ đọc; không phát sinh API write vào Tours trong task này.

### 8.2 SQL/database-first

- Full schema chuẩn trong Capstone_BE/database/tripmate_schema_v7.sql và migration riêng trong database/migrations. Không EF migration, không chạy lại full schema để nâng cấp DB có dữ liệu.
- Migration additive/idempotent: thêm `catalog.Destinations` và `commerce.TourDestinations` với PK/FK/unique/index; constraint whole VND và index query nếu preflight đạt; kiểm tra object đã tồn tại có đúng shape, không chỉ bỏ qua khi thấy tên. File draft migration cột đơn hiện tại phải được thay thế trước khi áp vào DB thật.
- Preflight phát hiện dữ liệu giá lẻ, constraint/trạng thái/counter không hợp lệ và timezone chưa được xác nhận. Không tự sửa dữ liệu nghiệp vụ, không seed demo vào DB thật.
- Cập nhật schema và migration trong cùng thay đổi; kiểm tra fresh DB và old schema → migrate → migrate lần hai ra cùng shape; giữ nguyên row/identity/FK ngoài scope.
- Legacy tour chưa gắn vùng không làm migration fail, nhưng bộ dữ liệu nghiệm thu filter phải được data owner gắn đúng. Backfill là script/data review riêng phối hợp UC-35, không heuristic tự động.
- Index xuất phát từ predicate thực và execution plan; cân nhắc Tours(status,published_at), TourSchedules(tour_id,status,start_datetime) INCLUDE capacity/end. Không mặc định claim contains search sẽ index seek hoặc tạo trùng index có sẵn.
- Rollback ứng dụng có thể giữ bảng liên kết/index additive. Không xóa vùng đã gắn hoặc làm tròn giá để rollback; constraint ảnh hưởng writer phải được phối hợp trước rollout.

### 8.3 Web và Mobile

Web dùng route page riêng /tours và BFF /api/tours cho browser; BFF chỉ gọi base URL BE được cấu hình server-side + fixed /api/v1/tours. Không query trực tiếp SQL từ FE, không cho client đổi upstream.

Client island quản lý form/state/cancel; server page giữ shell/metadata. Đọc searchParams theo API của phiên bản Next trong repo; client useSearchParams cần Suspense phù hợp. Không đưa toàn bộ app thành client.

BFF kiểm tra status/JSON/schema, no-store, timeout/cancel, không forward auth/cookie không cần thiết. Mất config hoặc BE down là error, không fallback fixture. Cấu hình Firebase hiện hữu phải qua full production build cả /admin/login, không chỉ build trang mới.

Mobile dùng Page → Cubit/Bloc → UseCase → Repository → RemoteDataSource → DioClient, đăng ký DI. Dùng Future<T> và Failure hiện có; không thêm Either/HTTP client thứ hai. Cancellation/version guard xử lý race và dispose.

Failure hiện tại là sealed, ValidationFailure chỉ có message. Nếu cần field errors, mở rộng cùng file core/error/failures.dart bằng named optional fieldErrors và test tương thích, không subclass sealed/final từ file khác.

## 9. Acceptance criteria và ma trận tình huống bắt buộc

Mỗi ID dưới đây phải có test/evidence trong plan. “Pass” phải kèm môi trường, head và command; ảnh đẹp hoặc HTTP 200 đơn lẻ không đủ.

| ID | Tình huống | Kết quả yêu cầu / nơi kiểm tra |
| --- | --- | --- |
| AC-01 | Guest, Traveler, optional token hỏng/hết hạn | Public search hoạt động; protected routes giữ nguyên; API + navigation |
| AC-02 | Draft/Pending/Rejected/Inactive, publish null/tương lai, operator chưa Approved/không Active | Không có trong items/count; SQL + endpoint |
| AC-03 | Không nhập filter | Public list đúng, default page 1/20; không ghi DB |
| AC-04 | Destination trim/NFC/casing/dấu; tour cũ không có vùng; tour nhiều vùng | Đúng D1 sửa đổi; tìm Đà Nẵng/Hội An đều trả tour Đà Nẵng → Hội An một lần; SQL Unicode tests |
| AC-05 | Destination %, _, [, ], quote và chuỗi injection | Literal matching trên **mọi vùng được gắn**, không nới điều kiện/nhân đôi tour/lỗi SQL |
| AC-06 | Destination quá 300 / surrogate boundary | Client inline + server 400, không truncate |
| AC-07 | Ngày hợp lệ và hai biên UTC 17:00 | Match đúng ngày VN, upper bound exclusive |
| AC-08 | Ngày hôm nay, lịch đã bắt đầu, ngày quá khứ, leap date sai, min/max date | Không lịch quá hạn; 200 empty hoặc 400 đúng trường; không overflow 500 |
| AC-09 | Không lịch / lịch Cancelled/Completed / all full / lịch full sớm nhưng còn chỗ muộn | Đúng D4/D5, không duplicate tour |
| AC-10 | Giá 0, chỉ min/chỉ max/equal, đúng biên trên | Inclusive; 0 không bị bỏ; SQL + client |
| AC-11 | Min > max, âm, phần lẻ, exponent, nonnumeric, overflow | 400 field errors; list cũ không bị đổi |
| AC-12 | Ba filter kết hợp | AND, count và card đúng cùng predicate |
| AC-13 | > 40 tour, title trùng, nhiều schedule mỗi tour | 20/trang, tie-break ID, không duplicate giữa trang khi dataset không đổi |
| AC-14 | page 0/âm/rỗng/abc, pageSize 0/101, offset overflow, ngoài totalPages | Validation hoặc empty out-of-range theo §4.6 |
| AC-15 | Duplicate query key / unknown filter | 400 rõ; client không hiển thị filter giả đang hoạt động |
| AC-16 | Capacity/publication đổi giữa count và items; giữa hai requests | Trong request nhất quán; request sau thấy thay đổi; không dirty read |
| AC-17 | Capacity riêng tour không đáng tin / toàn DB không truy cập | unknown có giới hạn hoặc request failure; không giả available |
| AC-18 | ID > 2^53, null schedule/date/slots, unknown enum, DTO malformed | ID giữ nguyên chuỗi; null đúng; client báo contract error |
| AC-19 | GET lặp/refresh/double submit | Không create/update Tour/Booking/TourSearchRequests; không idempotency table |
| AC-20 | Gõ 10 ký tự rồi submit/Enter | Không request từng phím; một submitted search logic |
| AC-21 | A chậm B nhanh, đổi page khi request chạy, dispose | Chỉ kết quả hiện hành cập nhật, không crash/emit sau dispose |
| AC-22 | 400, 500, offline, timeout, HTML 200 từ upstream | Inline hoặc MSG127 đúng, giữ state, không success/empty giả |
| AC-23 | Clear/Retry/draft khác applied/URL Back Forward | Đúng tiêu chí request và nhãn list; không tự áp draft chưa submit |
| AC-24 | Guest/Traveler → card → UC-26 → Back | Đúng tourId, không login bắt buộc khi browse, giữ page/filter/scroll và revalidate |
| AC-25 | No-store, resume/back/BFCache, capacity đã đổi | Không tái sử dụng availability cũ như live |
| AC-26 | Web widths, mobile widths, zoom/text scale, keyboard/safe area | Không tràn/cắt chức năng; keyboard/screen-reader sử dụng được |
| AC-27 | Empty/no-image/no-rating, English resource, date/time/VND | Không fake photo/rating/discount; đúng CR-07–09 và text approved |
| AC-28 | Fresh SQL + old schema migration + rerun + dữ liệu xấu | Schema tương đương, migration fail-safe, không mất row/identity/FK |
| AC-29 | Full build local sạch và CI, Firebase/env missing | Có bằng chứng build cả app; missing config báo đúng, không dummy secrets |
| AC-30 | Real SQL → BE → BFF/Web và Dio/Mobile | IDs/filter/price/schedule/capacity trên UI khớp row kiểm chứng; không chỉ test mocked HTTP |
| AC-31 | 200 concurrent requests, dataset benchmark, nhiều schedule | Đo NFR và lock/timeout; không N+1/toàn bảng trong memory; không claim khi chưa chạy |
| AC-32 | Rà response/log/PR artifacts | Không token/connection string/PII nội bộ; contract/docs/test counts khớp final head |

## 10. Phụ thuộc và rủi ro phải quản lý

| Phụ thuộc | Tình trạng quan sát | Điều kiện giải quyết |
| --- | --- | --- |
| TM-81 / UC-35 Create Tour Package | Jira To Do tại lần đọc | Writer cho Tour Operator chọn nhiều vùng từ danh mục và giữ đồng bộ khi sửa lịch trình; UTC schedules + VND nguyên; fixture SQL chỉ dùng test/dev |
| TM-83 / UC-37 và TM-106 / UC-60 | Jira To Do tại lần đọc | Làm rõ publication workflow và eligibility; không TM-70 tự approve/publish tour |
| TM-72 / UC-26 Tour Details | Jira To Do tại lần đọc | Chốt route + ID contract, test handoff thật trước release tích hợp |
| Writer giữ/nhả chỗ | Chưa xác minh cơ chế cập nhật counter trên DB thật | Xác nhận reserved_capacity phản ánh đúng hold/cancel/expire, không double-count |
| Thiết kế UC-24 | Có SRS/Stitch Mobile tham khảo, chưa verified Figma frame Web/Mobile | G1: mapping frame/component/field, chỉnh responsive và sanitation trước code UI |
| Firebase / CI FE | Baseline config import có thể làm prerender fail khi thiếu env; CI develop chưa chạy npm test | G2 baseline: full build và CI test step; sửa theo scope được duyệt, không bypass |
| Các PR TM-58 đang review | Không dùng làm base ngầm | Feature phải build trên develop; nếu cần dependency chưa merge, ghi rõ và chờ |

NFR tham chiếu SRS §4.2: API average <= 300ms, peak <= 2,000ms, 200 CCU; LCP <= 2.5s trên 4G. Đây là **mục tiêu cần đo**, không phải kết quả đã đạt. Chuẩn bị benchmark reproducible, tách cold/warm và ghi hardware/dataset; không dùng ngưỡng CSP 2,500ms cho Tour Search.

Không suy diễn có 0 bug sau checklist. Mục tiêu là giảm lỗi lặp lại bằng quyết định có căn cứ, test tái lập và evidence trung thực.

## 11. Duyệt, bàn giao và thay đổi sau duyệt

- [x] Phạm vi D0 đã được người dùng chọn.
- [x] D1–D8 bản đầu được owner chốt ngày 15/09/2026.
- [x] Owner chốt nghiệp vụ D1 sửa đổi ngày 19/09: Tour Operator chọn mọi vùng tour đi qua.
- [ ] Owner duyệt lại DB/API contract và test matrix D1 sửa đổi trong bản spec/plan này trước khi đổi implementation.
- [ ] G1 design audit đúng UC-24 hoàn tất; không lấy mockup POI thay Tour.
- [ ] Cập nhật tài liệu canonical trong Capstone_Docs theo plan, không đè thay đổi TM-58.

Sau duyệt, thay đổi field/filter/visibility/capacity/date/price là contract hoặc business change: sửa spec + plan + acceptance matrix trước code. Không thêm “tiện thể” booking, map, categories, ảnh, keyword hoặc rename nhánh người khác.

**Trạng thái 20/09/2026:** Database/Backend trong worktree `Capstone_BE_tm70` đã đổi sang D1 nhiều vùng; local full SQL-enabled suite **283/283 pass, 0 skip**, SQL-only **28/28 pass, 0 skip**. Chưa migrate TripMateDb cloud, chưa backfill dữ liệu vùng thật, chưa đo tải AC-31, chưa xác minh UTC schedule/`reserved_capacity`, chưa có CI trên head được push. Chưa commit, push hay tạo PR; Web/Mobile để Phase 2.
