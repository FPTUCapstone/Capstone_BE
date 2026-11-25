# Admin quản lý mẫu hợp đồng đối tác
Trạng thái: ĐÃ ĐƯỢC NGƯỜI DÙNG DUYỆT. Ngày: 23/11/2026.
Nhánh: fix/linhnv-operator-sign-up. Phạm vi: BE và FE web; không có chức năng Admin trên Mobile.

## 1. Mục tiêu và quan hệ với luồng ký
Admin tải mẫu DOCX, kiểm tra biến, xem PDF, lưu nháp và công bố phiên bản. Staff duyệt TO sẽ sử dụng phiên bản đang công bố để phát hành hợp đồng riêng.
Đây là bước nền tảng trước luồng ký, không triển khai OTP/chữ ký hoặc tự đổi tài khoản TO trong chức năng này.
Thay thế quyết định E cũ (chưa có quản trị mẫu) trong operator-contract-signing-spec.md. Chỉ các lựa chọn đã nêu trong chat được coi là đã chốt; các giới hạn kỹ thuật bên dưới là đề xuất cần duyệt.
Mẫu hợp đồng chính thức do chủ sản phẩm cung cấp; dữ liệu demo không được công bố như hợp đồng thật.

## 2. Vai trò và màn hình
- Administrator: danh sách, tạo/sửa nháp, tải DOCX, yêu cầu xem trước, công bố, tạo phiên bản mới và xem/tải lịch sử.
- Staff: không sửa hoặc công bố mẫu. Khi duyệt TO, BE sử dụng mẫu Published; quyền xem mẫu của Staff chỉ cấp nếu cần cho nghiệp vụ riêng.
- TO: không truy cập API quản trị mẫu; chỉ xem hợp đồng riêng ở chức năng ký.
- Route đề xuất: /admin/contract-templates và /admin/contract-templates/{id}; menu “Mẫu hợp đồng đối tác”.
- Kiểm tra quyền tại BE và BFF, không chỉ ẩn menu.

## 3. Vòng đời
Trạng thái: Draft -> Published -> Archived.
Draft có thể sửa tiêu đề, ghi chú, file nguồn. Mỗi lần thay đổi tạo revision mới; reset preview và xác nhận xem trước.
Published và Archived bất biến về nội dung/file/biến. “Tạo phiên bản mới” sao chép thành Draft mới; không sửa bản cũ.
Chỉ một phiên bản Published hiện hành cho loại OperatorPartnership và ngôn ngữ được hỗ trợ. Phase 1 chỉ tiếng Việt, chưa đa loại hợp đồng.
Công bố phiên bản mới chuyển bản Published trước đó sang Archived trong cùng transaction. Không xóa file hay hợp đồng đã tạo từ bản cũ.
Không có nút tắt bản Published duy nhất trong phase 1; không làm mất mẫu đang dùng.
Cho xóa nháp chưa từng được tham chiếu, sau xác nhận; không xóa Published/Archived. Nếu chưa cần xóa nháp có thể hoãn, nhưng không dùng xóa làm cơ chế sửa hợp đồng đã ký.
Mã phiên bản do BE cấp theo loại mẫu, không nhập tùy ý; revision nháp và version phát hành là hai khái niệm riêng.

## 4. Luồng Admin
1. Mở danh sách: mã/phiên bản, tiêu đề, trạng thái, cập nhật bởi/lúc, công bố bởi/lúc, trạng thái xử lý.
2. Tạo Draft với tiêu đề và DOCX.
3. Hệ thống kiểm tra file, trích biến, hiển thị biến hợp lệ, biến lạ, biến bắt buộc còn thiếu.
4. Admin sửa file Word và tải lại nếu lỗi; chưa cho công bố.
5. Admin yêu cầu tạo PDF xem trước bằng bộ dữ liệu mẫu được đánh dấu rõ “BẢN XEM TRƯỚC — KHÔNG DÙNG ĐỂ KÝ”.
6. Admin mở PDF, kiểm tra tiếng Việt, bố cục, trang, bảng, vùng chữ ký và các biến.
7. Nhấn Công bố -> xác nhận đây là nội dung chính thức; BE kiểm tra lại revision/hash/render và quyền.
8. Thành công: phiên bản mới Published, bản cũ Archived, ghi audit; danh sách cập nhật.
9. Muốn thay đổi: chọn Tạo phiên bản mới, chỉnh Draft rồi lặp lại. Không ghi đè hợp đồng TO đã phát hành.

## 5. File đầu vào và biến
Phase 1 chỉ .docx; không .doc/.docm/PDF làm mẫu đầu vào. Đề xuất tối đa 10 MB file nén, 50 MB sau giải nén, 2000 ZIP entries; kiểm tra giới hạn thực tế qua spike trước khi duyệt.
Kiểm tra extension + MIME + cấu trúc OOXML thực, không tin tên file. Giới hạn ZIP bomb/path traversal, tắt external relationships/network fetch, chặn macro/OLE/embedded file và field thực thi ngoài whitelist. Quét file theo khả năng hạ tầng.
Biến dạng {{company_name}}. Parser phải hỗ trợ token bị Word chia qua nhiều text runs; không thay chuỗi XML mù hoặc làm hỏng định dạng.
Whitelist đề xuất:
- contract_number, contract_version;
- company_name, tax_code, business_license_no, business_address;
- representative_name, representative_title, representative_capacity;
- operator_email;
- signature_block.
Biến lặp lại được phép. Không cho expression/script, tự truy cập thuộc tính DB hoặc HTML tùy ý.
Tối thiểu bắt buộc: contract_number, company_name, tax_code, representative_name, signature_block. Danh sách chính thức cần chủ sản phẩm duyệt theo mẫu thực.
Thông tin ngân hàng, tỷ lệ hoa hồng và điều khoản tài chính không tự suy ra: chỉ thêm biến khi đã có nguồn và quy tắc được duyệt. Không nhúng ngày ký giả khi phát hành; timestamp ký do luồng ký quyết định.
Vùng signature_block phải có cách định vị ổn định ở bước ký; DOCX->PDF cần trả metadata vị trí hoặc giải pháp tương đương. Nếu engine không làm được thì dừng thiết kế để chốt giải pháp, không đặt ảnh bằng tọa độ cố định cho mọi bản mẫu.
Tọa độ vùng ký phải tính lại trên PDF hợp đồng thực tế sau khi điền dữ liệu TO. Không tái sử dụng tọa độ của preview dữ liệu mẫu: tên/địa chỉ dài có thể đổi trang hoặc vị trí.

## 6. Xem trước và xử lý bất đồng bộ
RenderState: NotRequested, Processing, Ready, Failed. Lưu job revision/hash; kết quả job cũ không được ghi đè revision mới.
Mỗi job có timeout, giới hạn CPU/RAM, trang và retry có kiểm soát; renderer cô lập, không truy cập Internet. Đề xuất timeout 60 giây, tối đa 100 trang; cần xác minh bằng mẫu thật.
DOCX gốc private, PDF xem trước private. Không dùng URL công khai lâu dài.
Preview chỉ dùng dữ liệu giả, không tự lấy thông tin của một TO thật. Mẫu đã công bố vẫn giữ DOCX nguồn và manifest biến bất biến; PDF preview không thay thế contract PDF riêng của từng TO.
Khi render thất bại: hiển thị lỗi thân thiện, tải lại/thử lại, giữ Draft; không công bố.
Nếu file thay đổi trong khi preview: invalidate Ready cũ. Chỉ công bố đúng revision/hash đã xem.
Trước publish Admin xác nhận đã kiểm tra preview; server lưu revision xác nhận, không coi việc cuộn cuối là bằng chứng pháp lý.

## 7. Dữ liệu và transaction đề xuất
OperatorContractTemplates:
id, template_type, language, version, draft_revision, title, status, row_version;
source_storage_key, source_sha256, variables_manifest_json, validation_status;
preview_storage_key, preview_sha256, preview_revision, render_state/error_code;
preview_acknowledged_revision, created/updated/published_by và timestamps UTC.
ContractTemplateRenderJobs: id/template/revision/hash/state/lease/attempt/next_attempt/result; dùng job/outbox pattern sẵn có, không tạo hạ tầng hàng đợi mới nếu chưa cần.
Có filtered unique index bảo đảm một Published/type/language. Constraint bảo đảm Published có source/validation/preview cùng revision.
Hash nội dung mẫu, file DOCX, preview PDF và PDF hợp đồng đã ký được lưu riêng theo đúng đối tượng; không yêu cầu chúng bằng nhau.
Không giữ DB transaction trong lúc render/upload. Stage file -> kiểm tra durable -> transaction metadata; cleanup orphan riêng.
Publish dùng optimistic concurrency và transaction cập nhật old/new + audit. Hai Admin công bố đồng thời chỉ một request thắng; request khác 409, không âm thầm ghi đè.
Request mang expectedCurrentPublishedId (null nếu lần đầu) cùng revision của publication head. BE kiểm tra/khóa head và cập nhật có điều kiện trong transaction; unique index một Published chỉ là lớp bảo vệ bổ sung. Hai Draft khác nhau cùng nhắm một head cũ cũng phải có đúng một request thắng.
Lỗi lưu file/DB không được mất bản Published hiện tại. Artifact đang được tham chiếu không bị cleanup.

## 8. API đề xuất — Administrator only
- GET /api/v1/admin/contract-templates?status=&page=&pageSize=
- POST /api/v1/admin/contract-templates — tạo Draft, metadata và DOCX.
- GET /api/v1/admin/contract-templates/{id}
- PUT /api/v1/admin/contract-templates/{id} — sửa Draft, expected revision/row version, file mới nếu có.
- POST .../{id}/preview — tạo job cho revision hiện tại; trả 202 nếu xử lý nền.
- GET .../{id}/documents/{source|preview} — authorized stream, disposition/type an toàn.
- POST .../{id}/publish — expected revision/hash/row version, preview acknowledgment; idempotency key.
- POST .../{id}/versions — tạo Draft mới từ bản Published/Archived.
- DELETE .../{id} — chỉ Draft không bị tham chiếu, nếu đưa vào phase 1.
Request publish không nhận status tự chọn, storage key hay người công bố từ client.
Lỗi: 400 file/biến không hợp lệ, 401 chưa đăng nhập, 403 sai quyền, 404 không tồn tại, 409 stale/not-draft/render-not-ready, 413 quá dung lượng, 503 storage/renderer unavailable.
Dùng Result/ProblemDetails chuẩn, không hiện stacktrace hoặc mã lỗi thô trên UI. Tên route cuối cùng xác minh theo conventions trước code.

## 9. Tích hợp với Staff approval — bước sau
Template publish riêng không thay tài khoản hoặc hồ sơ TO.
Luồng ký sau này lấy đúng Published template tại thời điểm Staff duyệt, lưu immutable snapshot template version/hash và dữ liệu TO. Publish mới sau đó không tự đổi contract đã phát hành.
Nếu chưa có Published template: sau khi bật chức năng ký, Staff approval phải báo rõ chưa có mẫu, không vừa Approved vừa không có hợp đồng.
Trong phase chỉ quản trị mẫu, giữ luồng duyệt hiện tại; không bật trạng thái Chờ ký trước khi BE/clients ký sẵn sàng.
Các TO Active cũ không bị chuyển trạng thái bởi publish.

## 10. UI, audit và bảo mật
Giữ giao diện Admin hiện có, bảng có loading/empty/error/retry và phân trang. Upload tiến trình, xử lý render, preview, lỗi biến, confirm publish và version history rõ ràng.
Ngày dd/MM/yyyy, giờ có timezone; API UTC ISO. Lưu form khi thao tác lỗi, cảnh báo rời trang có sửa nháp.
File picker và preview có bàn phím, nhãn, liên kết tải thay thế nếu PDF không nhúng được; viewport nhỏ không che nút.
Audit DraftCreated, DraftUpdated, PreviewRequested/Failed, TemplatePublished, VersionCreated, DraftDeleted với actor/id/revision/hash/result. Không log full DOCX/nội dung hợp đồng hoặc credential.
Không đưa DOCX/PDF thật chứa dữ liệu nhạy cảm vào Git. File test phải giả.
Retention và cleanup không xóa bằng chứng/nguồn đang được hợp đồng tham chiếu.

## 11. Tiêu chí chấp nhận
- Admin tạo Draft DOCX hợp lệ; Staff/TO không có quyền quản trị.
- File giả, macro, ZIP bomb và biến lạ/thiếu bắt buộc bị chặn; không render nội dung ngoài.
- Token bị chia text runs vẫn nhận diện/thay đúng; tiếng Việt/font/bảng/ngắt trang xem đúng.
- File mới làm preview cũ mất hiệu lực; job cũ không thể publish revision mới.
- Chỉ Ready + đúng revision đã xác nhận mới publish; lỗi không ảnh hưởng Published hiện tại.
- Hai publish đồng thời không sinh hai Published; retry cùng key cùng kết quả.
- Tạo version mới không sửa DOCX/PDF của bản đã công bố hoặc hợp đồng TO.
- Download kiểm tra quyền; bản xem trước có watermark rõ và không dùng làm tài liệu ký thật.
- Migration chạy lại an toàn và giữ dữ liệu; không tác động tài khoản TO trong phase này.

## 12. Điểm cần duyệt trước code
- Mẫu DOCX thật và whitelist/trường bắt buộc.
- Giới hạn upload/render đề xuất tại mục5/6.
- Engine DOCX->PDF, license/font tiếng Việt, private storage và cách xác định vùng ký phải qua spike.
- Cơ chế công bố: phase 1 đề xuất có hiệu lực ngay, một bản tiếng Việt, không lên lịch hiệu lực.
- Cho xóa Draft chưa tham chiếu hay chỉ giữ lịch sử.
- Không coi việc duyệt spec quản trị mẫu là duyệt OTP/phương thức ký của luồng TO.
