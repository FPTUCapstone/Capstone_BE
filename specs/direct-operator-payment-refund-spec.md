# Thanh toán trực tiếp TO, refund và hoa hồng — BE / đặc tả nghiệp vụ chung

Ngày: 25/11/2026. Nhánh: `fix/linhnv-payout-and-refund`.
Trạng thái: **Đã duyệt nghiệp vụ ngày 25/11/2026, gồm Admin đối soát tranh chấp refund; Staff chỉ theo dõi. Chưa triển khai hoặc đổi dữ liệu thật.**

## 1. Phạm vi và nguồn yêu cầu

Đây là change request thay mô hình TripMate giữ tiền tour bằng mô hình Traveler trả trực tiếp TO. Không khẳng định luồng mới đã có trong SRS. Khi được duyệt, phải cập nhật SRS, ma trận UC và các quy định cũ liên quan trước phát hành.

Nguồn: quyết định người dùng trong chat ngày 25/11/2026, code hiện tại và tài liệu [payOS API](https://payos.vn/docs/api/), [xác minh chữ ký](https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/).

Tài liệu này là nguồn nghiệp vụ/API chung cho hai spec cùng tên trong FE và Mobile. Các route, entity và DTO mới dưới đây là **đề xuất**, chưa phải contract đã triển khai.

### Đã chốt bởi người dùng

- Thanh toán tour qua **payOS thật**, tiền đi trực tiếp tới TO; TripMate không giữ tiền tour trong luồng mới.
- TO hoàn trực tiếp cho Traveler; TripMate theo dõi tiến trình và thu hoa hồng tháng.
- Khiếu nại: TO tối đa 2 đề xuất (ban đầu + 1 chỉnh sửa); Traveler counter-offer tối đa 1 lần. TO có đủ lựa chọn không hoàn / hoàn một phần / hoàn toàn bộ. Lý do bắt buộc cho đề xuất một phần và khi Traveler từ chối.
- Sau khi TO báo đã chuyển hợp lệ, Traveler có **72 giờ liên tục** để xác nhận hoặc phản đối. Không phản hồi đúng hạn thì hệ thống **tự hoàn tất** refund.

### Đề xuất nghiệp vụ đã được người dùng duyệt ngày 25/11/2026

- Kết nối payOS sau ký hợp đồng, bắt buộc trước mở bán; cho tạo tour nháp khi chưa kết nối.
- Giữ chỗ và link thanh toán 15 phút theo đồng hồ thực UTC của server.
- Hoàn toàn bộ không chờ Traveler đồng ý số tiền; hoàn một phần sau thương lượng phải được chấp nhận. Hủy theo chính sách đã lưu không thương lượng lại mức hoàn.
- TO từ chối hoàn phải nhập lý do. Giới hạn thương lượng chỉ áp dụng khiếu nại sau tour. Hết lượt còn bất đồng thì giữ tranh chấp khiếu nại; Staff theo dõi/nhắc, không tự chốt số tiền hoặc hoàn tất refund.
- Hạn TO chuyển: 72 giờ liên tục sau khi đủ thông tin nhận tiền; nhắc Traveler lúc báo chuyển và trước hạn 24 giờ.
- Hoa hồng ghi nhận khi tour hoàn thành, trừ refund đã hoàn tất; tạm treo dòng đang tranh chấp/chờ hoàn; hạn thanh toán đối soát tháng 7 ngày.

Ngoài phạm vi: tự động chi hộ/refund qua ngân hàng, tự động trích nợ TO, đa cổng thanh toán, refund một phần từng hành khách nếu chưa có UC được duyệt, thay đổi commercial-service bookings. Không suy diễn quyền thu tiền payOS thành quyền chi tiền.

## 2. Hiện trạng đã đối chiếu

- `Features/Traveler/Payments/Create/CreateOrReusePaymentLinkCommandHandler.cs` dùng `IPaymentGateway`; `Infrastructure/Services/Payment/PayOsPaymentGateway.cs` là điểm tích hợp hiện tại. Phải kiểm tra DI/options toàn bộ trước chuyển sang kênh theo TO.
- `PayOsWebhookController.cs` nhận tại `/api/v1/payments/payos/webhook`; phải giữ cho giao dịch cũ.
- `OperatorRefundsController.cs` có `/confirm`, `/return`; `ReportBookingRefundReturnCommand.cs` chỉ nhận mã tham chiếu và nghĩa là TO trả phần chia về TripMate.
- `RefundStatus.cs`, `RefundShareCalculator.cs`, `RefundProcessor.cs`, `RefundProcessingBackgroundService.cs` thuộc pipeline cũ. Không đổi nghĩa các trạng thái/endpoint cũ sang chuyển thẳng cho khách.
- UC-44 hiện báo cáo theo thời điểm thanh toán; không âm thầm đổi tất cả báo cáo sang tháng hoàn thành tour. Phân biệt báo cáo bán hàng và sổ hoa hồng mới.
- `specs/UC-42-confirm-booking-refund-srs-alignment-be-spec.md` ghi UC-67 là refund; `specs/UC-67-spec.md` lại mang nội dung statistical reports. **Không dùng số UC làm khóa định tuyến nghiệp vụ**; cần sửa đối chiếu SRS trước phát hành, không ghi đè tài liệu lịch sử trong task này.

## 3. Phân quyền

| Actor | Hành động |
|---|---|
| Traveler chủ booking | Xem tiến trình, xác nhận tài khoản nhận, xác nhận đủ tiền, phản đối |
| TO chủ tour | Kết nối kênh của mình, xem nghĩa vụ hoàn, báo chuyển và bổ sung giao dịch còn thiếu |
| Staff | Chỉ xem tiến trình refund, bằng chứng và tranh chấp, theo dõi/nhắc các bên; không xác nhận chuyển/nhận, quyết định amount hoặc hoàn tất thay hai bên |
| Administrator | Chính sách hoa hồng, đối soát tháng, điều chỉnh có audit; không tự mặc định có quyền Staff |
| System | Webhook xác minh, expiry, nhắc hạn, tự hoàn sau 72h, lập đối soát |

Administrator có quyền riêng `DirectRefundDisputeReview`: đọc hồ sơ/bằng chứng refund bị tranh chấp và ra kết quả đối soát. Quyền này không phải kế thừa toàn bộ quyền Staff, không mở quyền đọc tùy ý mọi chứng từ và không cho sửa tùy ý amount đã chốt của khiếu nại; quyền quyết định khiếu nại chưa chốt được cấp riêng theo mục 13.

Ownership được lấy từ DB; không tin `operatorId`, amount hoặc refund eligibility từ client. Hạn chế mở bán vì công nợ không được chặn TO thực hiện nghĩa vụ hoàn. Tài khoản bị khóa cần quy trình hỗ trợ quyền truy cập riêng; không cấp cho Staff quyền xác nhận refund thay TO/Traveler.

## 4. Kết nối payOS thật theo TO

1. TO đã được duyệt, ký và Active mở cấu hình thanh toán.
2. TO cung cấp Client ID, API Key, Checksum Key của kênh riêng qua HTTPS. Không nhận mật khẩu ngân hàng/payOS.
3. Backend kiểm tra API và đăng ký/xác nhận webhook kênh. Kết nối API thành công **không tự chứng minh chủ tài khoản ngân hàng**; cần đối chiếu tên bên nhận với hồ sơ doanh nghiệp và kiểm chứng theo khả năng payOS trước đánh dấu Verified.
4. Lưu phiên bản kênh đã mã hóa; không trả khóa sau lưu, không ghi khóa vào log/audit/FE/Mobile. Khóa mã hóa nằm trong secret store ngoài DB; có kế hoạch xoay khóa.
5. Trạng thái dự kiến: `NotConnected`, `PendingVerification`, `Ready`, `ConnectionError`, `Disabled`. Chỉ `Ready` cho mở bán/tạo link mới.
6. Mỗi payment attempt chụp bất biến channelVersion, TO, beneficiary, amount, currency, booking, commission policy/rate và provider reference. Đổi khóa/kênh không đổi giao dịch đang chờ; giữ khả năng xác minh/đối soát kênh cũ.

Không dùng một kênh TripMate để tạo QR đến ngân hàng tùy ý. Trước code phải kiểm chứng thực tế mô hình nhiều kênh, webhook và điều kiện nhà cung cấp; không giả định có OAuth/merchant delegation.

## 5. Đặt tour và thanh toán

`PendingPayment → Paid/Confirmed` hoặc `Expired/Cancelled` theo booking hiện tại.

- Kiểm tra tour hợp lệ, giá/voucher/chỗ; tạo booking, giữ chỗ atomic. Link và booking dùng cùng deadline 15 phút.
- Chọn kênh theo chủ tour trên server. Hiển thị người nhận trước chuyển khoản. Không fallback về tài khoản TripMate khi TO lỗi.
- Tạo/reuse payment link idempotent; không giữ SQL transaction trong lúc gọi mạng. Timeout phải tra cứu provider trước tạo lại để tránh nhiều link ngoài ý muốn.
- Callback URL chỉ dùng điều hướng. Hoàn tất dựa webhook ký hợp lệ hoặc đối soát API server-to-server; xác minh channel + order + paymentLinkId + beneficiary + VND + đúng số tiền.
- Endpoint webhook có định danh kênh opaque hoặc ánh xạ order/payment bất biến để chọn đúng khóa; định danh trên URL không phải bằng chứng xác thực.
- Webhook lặp/replay không phát lại vé, không cộng tiền/hoa hồng hai lần. Persist event/operation và hiệu ứng trong transaction, notification qua cơ chế delivery bền vững.
- Thiếu/thừa tiền, tiền đến sau hạn/hủy, hoặc hết chỗ: `PaymentReconciliation`; không phát vé tự động. Giao dịch vẫn được ghi nhận và xử lý xác nhận/hoàn theo kết quả đối soát. Không bỏ tiền đến muộn vì booking đã hết hạn.
- Production không bật mock; không dùng thời gian giả lập để đặt `expiredAt` cho payOS thật. Đồng hồ nghiệp vụ và đồng hồ provider phải được phân biệt rõ.

## 6. Tạo nghĩa vụ refund

Nguồn gồm hủy theo snapshot chính sách, TO hủy, sự kiện khẩn cấp hiện có, kết quả khiếu nại. Mỗi nguồn chỉ tạo một nghĩa vụ cho cùng operation; giữ liên kết nguồn và lịch sử quyết định.

- Booking chưa có khoản tiền xác minh: hủy được nhưng không tạo refund có tiền. Sai lệch thanh toán đi đối soát.
- `0 < refundAmount ≤ verifiedPaidAmount − previouslyCompletedRefunds − otherReservedRefundObligations`. So sánh có khóa để không hoàn vượt khi nhiều nguồn chạy đồng thời.
- Hoàn toàn bộ nghĩa là phần tiền thực trả còn có thể hoàn; không dùng giá niêm yết trước voucher. Không tính thêm chia 70/90% hoặc phần TripMate bù cho direct payments.
- Khi thương lượng kết thúc, amount được khóa; không cho TO tự giảm khi báo chuyển.
- Hủy phải vô hiệu vé, giải phóng chỗ theo quy tắc hiện có ngay; không đợi refund hoàn tất. Khiếu nại sau tour không biến booking hoàn thành thành hủy.

## 7. Vòng đời refund trực tiếp

Trạng thái nội bộ đề xuất tách khỏi enum cũ:

`AwaitingRecipient → AwaitingTransfer → AwaitingTravelerConfirmation → Completed`

Nhánh: `AwaitingTravelerConfirmation → Disputed`; khi Traveler xác nhận đã nhận đủ hoặc Admin kết luận đủ căn cứ hoàn thành thì `Completed`. Nếu Admin xác định chuyển lỗi/thiếu/sai tài khoản thì yêu cầu TO khắc phục, lưu kết quả đối soát và nghĩa vụ còn lại; chưa đủ căn cứ thì giữ `Disputed`. Staff chỉ theo dõi. Không tự tạo lại cửa sổ 72h sau phản đối.
`IsOverdue` là cờ suy ra từ hạn chuyển, không phải trạng thái hoàn thành.

### 7.1 Thông tin nhận và hạn chuyển

- Traveler chủ booking xác nhận bank code, account number, account holder; chỉ kiểm tra định dạng không được gắn nhãn đã xác minh chủ tài khoản nếu không có dịch vụ kiểm chứng.
- Chụp snapshot thông tin nhận cho từng transfer report. Cho sửa trước khi có báo chuyển, lưu audit; không lùi deadline khi sửa. Nếu thay đổi làm giao dịch đang chuyển không rõ ràng thì chuyển đối soát.
- Deadline chuyển bắt đầu tại thời điểm cả amount đã chốt và recipient hợp lệ. Đã duyệt 72 giờ liên tục theo Việt Nam; tính cả cuối tuần/lễ, không dùng lịch ngày làm việc.

### 7.2 TO báo đã chuyển

Request gồm `operationId`, `expectedVersion`, bank transaction reference, actualTransferredAt, amount, evidence upload ID(s). Chỉ chủ tour được thao tác.

- Bắt buộc bằng chứng, reference không trống, amount đúng phần còn thiếu, transferredAt không ở tương lai; không chấp nhận báo chuyển chỉ có mã tự nhập.
- Evidence riêng tư, kiểm tra MIME/magic bytes, dung lượng/số file theo policy mục 13; refund participants, Staff và Admin có quyền đối soát tranh chấp được đọc theo phạm vi hồ sơ, không qua URL public.
- Trong transaction: lưu transfer immutable, recipient snapshot, báo chuyển, audit, notification; chuyển `AwaitingTravelerConfirmation`.
- `ReportedAtUtc` lấy từ server; `ConfirmationDeadlineUtc = ReportedAtUtc + 72 giờ`. Không lấy ngày chuyển do TO nhập làm mốc đếm.
- Thông báo **Đã ghi nhận thông tin chuyển khoản**, chưa phải hoàn thành.
- Không sửa/xóa transfer đã báo; retry cùng operation trả cùng kết quả. Bổ sung chứng cứ không reset deadline. Sau phản đối, không tự tạo lại vòng 72h chỉ vì TO bổ sung bằng chứng hoặc báo chuyển; giao dịch bổ sung theo mục 13 đã duyệt.

### 7.3 Traveler phản hồi và tự hoàn 72 giờ

- Xác nhận đã nhận đủ trước hạn → `Completed`, `CompletionMethod=TravelerConfirmed`.
- Phản đối trước hạn: `NotReceived` hoặc `PartiallyReceived`, bắt buộc lý do; trường hợp thiếu tiền nhập số thực nhận, chứng cứ có thể bổ sung. Chuyển `Disputed`, hủy khả năng auto-complete của vòng đó.
- Không phản hồi: tại `now >= ConfirmationDeadlineUtc`, hệ thống chuyển `Completed`, `CompletionMethod=AutoCompletedAfter72Hours`.
- Phản hồi trong cửa sổ khi `now < deadline`; đúng mốc deadline thuộc auto-completion. Client đếm ngược không quyết định quyền thao tác.
- Job và phản hồi dùng cùng khóa/rowversion, kiểm tra lại status + deadline trong transaction; chỉ một kết quả thắng, không hạch toán hai lần. Nếu job chạy trễ, request sau hạn phải trả trạng thái hiệu lực theo deadline, không kéo dài 72h.
- Job có thể chạy lại sau restart/downtime; quét theo deadline UTC persisted, xử lý theo batch; không phụ thuộc app mở, Redis timer hay notification gửi thành công.
- Gửi lúc báo chuyển, nhắc trước hạn 24h, và khi hoàn tất. Retry notification không reset hạn.
- UI phải ghi **Tự động hoàn tất do hết hạn phản hồi**, không ghi đã được ngân hàng xác minh. Quy tắc 72h hiển thị rõ trước xác nhận recipient và trong thông báo báo chuyển.

### 7.4 Đối soát và thiếu tiền

- Staff chỉ xem dữ kiện hai phía, theo dõi và nhắc; không có mutation resolve/complete thay hai bên và không có completion method `StaffResolved` cho direct refund.
- Khi TO đã báo chuyển và Traveler xác nhận nhận đủ: hoàn tất với `TravelerConfirmed`, kể cả đang Disputed.
- Admin được xét hồ sơ `Disputed`, yêu cầu hai phía bổ sung bằng chứng hoặc kết quả tra soát ngân hàng. Không mặc định tin ảnh TO tải lên, không mặc định khách phản đối là gian lận, và không bắt TO chuyển lần hai khi chưa xác định giao dịch cũ bị lỗi/thiếu.
- Admin chỉ kết luận đã hoàn khi có đủ căn cứ: đúng giao dịch, đúng tài khoản nhận đã xác nhận, đủ số tiền, kết quả thực hiện/tra soát phù hợp; lưu `CompletionMethod=AdminResolved`, decision reason, evidence IDs, actor/time và audit bất biến; thông báo cả hai bên.
- Nếu kết luận chuyển lỗi/thiếu/sai tài khoản: ghi phần đã xác minh và phần nghĩa vụ còn lại, yêu cầu TO khắc phục. Nếu chưa đủ căn cứ: giữ `Disputed`, ghi kết quả yêu cầu bổ sung, không tạo credit hoa hồng như đã hoàn.
- Admin resolve cần operationId + expectedVersion + transaction/lock chung với Traveler response và financial effects. Race/retry chỉ hoàn thành/hạch toán một lần; không tự sửa recipient, refund amount hoặc xóa bằng chứng. Kết quả giải quyết này là quyết định vận hành nền tảng, không mô tả là phán quyết của cơ quan nhà nước.
- Nếu nhận thiếu: lưu số tiền Traveler khai báo riêng với số tiền hai bên thống nhất, giữ nghĩa vụ phần thiếu và lịch sử; TO bổ sung, không cho tổng refund vượt hạn mức. Admin xác định phần thiếu theo bằng chứng; hạn chuyển bổ sung 72 giờ từ quyết định theo mục 13.
- Nếu chưa nhận/sai tài khoản: giữ nghĩa vụ chưa hoàn, yêu cầu TO xử lý; không tự đánh dấu Completed.
- Khi đã tự hoàn sau 72h: không cho client sửa ngược trực tiếp. Staff không có quyền mở lại/điều chỉnh refund theo phạm vi này; khách được gửi yêu cầu xem xét muộn trong 7 ngày tiếp theo; Admin xử lý qua hồ sơ liên kết theo mục 13, không sửa ngầm lịch sử.

## 8. Hoa hồng tháng và báo cáo

Đề xuất sổ công nợ hoa hồng riêng, không tái sử dụng nợ TO trả tiền về TripMate để refund.

- Eligible commission base = khoản đã xác minh của tour hoàn thành trừ phần refund đã hoàn tất; dòng có refund/complaint đang chờ được tạm treo khỏi chốt thu.
- `commissionDue = roundVnd(eligibleBase × snapshottedRate)`. Áp dụng round half up đến đồng theo từng booking như mục 13.
- Ví dụ 100.000đ, refund 39.000đ, rate 10% → base 61.000đ, hoa hồng 6.100đ, TO giữ 54.900đ sau hoàn và trả hoa hồng.
- TO đã nhận tiền từ đầu; khoản này không phải payout phải trả. Không tạo advance/final settlement 70/90% cho direct payments.
- Đối soát tháng theo Việt Nam; invoice đã chốt bất biến. Refund sau khi đã chốt sinh credit điều chỉnh liên kết dòng gốc, không sửa lịch sử; hỗ trợ cả invoice chưa trả và đã trả, không tính âm trùng.
- TO trả invoice hoa hồng qua kênh payOS TripMate, có payment purpose riêng `CommissionCollection`; không nhầm với `TourPayment`. Xác minh amount/invoice/TO/purpose, chống lặp như thanh toán tour.
- Hạn trả 7 ngày từ khi phát hành đối soát đã được duyệt; quá hạn tạm dừng nhận booking mới theo mục 13. Không khóa khả năng xử lý booking/hoàn tiền; không tự trích tiền ngân hàng TO.
- UC-44 sales vẫn có thể theo tháng khách trả; báo cáo commission theo tháng ghi nhận, cash collection theo tháng thực thu. DTO/nhãn phải phân biệt, không cộng lẫn doanh thu legacy/direct. Giữ snapshot báo cáo lịch sử; hiển thị nhãn mô hình khi cần.

## 9. Dữ liệu đề xuất

| Khái niệm | Dữ liệu chính |
|---|---|
| Payment flow snapshot | `PlatformCollectedLegacy` / `OperatorDirect`; policyVersion; channelVersion; purpose |
| OperatorPaymentChannel | owner, opaque ID, version, encrypted credential references, beneficiary snapshot, verification status |
| DirectRefundCase | booking/payment/source IDs, amount, recipient, status, transfer deadline, version |
| DirectRefundTransfer | operationId, caseId, amount/reference/time, private evidence IDs, immutable recipient snapshot, reportedAt, confirmationDeadline |
| DirectRefundResponse | transferId, actor, reason, receivedAmount, timestamp |
| Completion | completedAt, method, ruleVersion, causal transfer/event; adjustment linkage |
| Commission statement/lines | period, payment/refund IDs, rate/base/rounding snapshot, immutable totals, adjustments, due date, paid amount |

SQL idempotent, Database-First; tên schema/table phải đối chiếu convention trước triển khai. Unique operation/provider event/reference mapping, rowversion và index status/deadline. Migration không chứa credential thật, không xóa bảng legacy. Timestamp `datetime2` cần `.AsUtcDateTime2()` theo repository.

## 10. API đề xuất (chỉ dùng namespace mới khi cần tránh đổi nghĩa legacy)

| Method / route | Mục đích |
|---|---|
| GET/PUT `/api/v1/operator/payment-channel` | Trạng thái, tạo phiên bản cấu hình; write-only secret fields |
| POST `/api/v1/operator/payment-channel/verify` | Kiểm tra và xác minh kết nối |
| POST `/api/v1/payments/payos/channels/{opaqueId}/webhook` | Nhận sự kiện kênh TO/purpose; không tin URL làm auth |
| GET `/api/v1/traveler/direct-refunds/{id}` | Detail/timeline/actions/deadline cho chủ booking |
| PUT `/api/v1/traveler/direct-refunds/{id}/recipient` | Xác nhận recipient + acknowledgement quy tắc 72h |
| POST `/api/v1/traveler/direct-refunds/{id}/response` | `ReceivedInFull` / `NotReceived` / `PartiallyReceived` |
| GET `/api/v1/operator/direct-refunds[/{id}]` | Danh sách phân trang/detail của TO |
| POST `/api/v1/operator/direct-refunds/{id}/transfers` | Báo chuyển có operationId + version |
| POST `/api/v1/operator/direct-refunds/{id}/evidence` | Upload private evidence có validate |
| GET `/api/v1/direct-refunds/{id}/evidence/{evidenceId}` | Download có role/ownership kiểm tra server |
| GET `/api/v1/staff/direct-refunds[/{id}]` | Hàng chờ đối soát/quá hạn |
| GET `/api/v1/admin/direct-refund-disputes[/{id}]` | Admin có quyền riêng xem hồ sơ tranh chấp và bằng chứng liên quan |
| POST `/api/v1/admin/direct-refund-disputes/{id}/resolve` | Kết quả `Completed` / `CorrectionRequired` / `MoreEvidenceRequired`, lý do, căn cứ, operationId và expectedVersion; chỉ Completed hợp lệ mới hoàn tất |
| GET `/api/v1/operator/commission-statements[/{id}]` | Nghĩa vụ tháng/chi tiết của TO |
| POST `/api/v1/operator/commission-statements/{id}/payment-link` | TO trả hoa hồng TripMate |
| GET `/api/v1/admin/commission-statements[/{id}]` | Quản trị đối soát tháng |

Existing booking create/payment/status/cancel routes giữ contract tương thích; thêm flow/purpose/recipient/action metadata thích hợp. Upload/DTO contract phải hoàn thiện trong task contract trước client implementation.

Response refund cần: status, flow, refundAmount, confirmedReceivedAmount, remainingAmount, recipient masked theo quyền, timeline, activeTransfer, reportedAtUtc, confirmationDeadlineUtc, serverNowUtc, completionMethod, version, allowedActions. Mutation trả detail mới để không cần F5.

Lỗi feature codes đề xuất: `payment_channel.not_ready`, `.verification_failed`, `payment.reconciliation_required`, `direct_refund.invalid_state`, `.version_conflict`, `.response_window_closed`, `.recipient_required`, `.evidence_invalid`, `.amount_invalid`, `.not_found`, `.access_denied`. Dùng Result/HandleFailure hiện có; 409 cho conflict/state/window, 400 validation, 401/403/404 theo convention; lỗi hạ tầng không lộ secret/provider payload.

## 11. Chuyển đổi an toàn

- Feature flag mặc định tắt; chỉ bật sau duyệt SRS/contract, migration và kiểm chứng provider thật.
- Phân loại booking/payment cũ bằng dữ kiện tài chính, không chỉ ngày tạo. Không tự coi mọi booking mới chưa thanh toán là direct nếu đã có link legacy.
- Những đơn/link đã thu theo TripMate giữ flow legacy đến cuối, gồm payout/refund/debt cũ. Không đổi người nhận của link đang chờ.
- Booking direct không được legacy payout/refund workers chọn. Workers direct không chọn dữ liệu legacy. Kiểm tra tất cả entry points: hủy, weather/emergency, complaints, manual completion, reports, exports.
- Không backfill giả direct vào tiền đã thu. Rollback tắt tạo giao dịch direct mới; vẫn xử lý webhook/refund/commission direct tồn đọng, không đổi thành legacy.
- Backup DB và verify restore ở môi trường tách trước apply; dry-run migration, đối chiếu số dư trước/sau. Không reset DB hoặc xóa volume.

## 12. Tiêu chí chấp nhận

1. Hai TO dùng hai kênh thật; QR/beneficiary đúng từng chủ tour; không fallback TripMate.
2. Callback giả không làm Paid; webhook sai kênh/chữ ký/số tiền bị chặn/đối soát; webhook lặp chỉ ghi một lần.
3. Đặt tour giữ chỗ 15 phút thực; late payment được ghi nhận, không mất dấu tiền.
4. Refund tạo đúng nguồn, đúng amount sau voucher, không vượt tiền đã trả; mọi nhánh 0/70/90% legacy không áp cho direct.
5. TO không báo chuyển khi thiếu evidence/reference; thông báo chỉ xuất hiện sau persist thành công.
6. 72h từ server report time; Traveler xác nhận trước hạn hoàn tất; phản đối trước hạn ngăn job.
7. Không phản hồi thì đúng/qua hạn tự hoàn với method riêng; downtime/retry/concurrency không ghi trùng hoặc kéo dài hạn.
8. Sửa chứng cứ không reset hạn; thiếu tiền qua đối soát chỉ còn nghĩa vụ phần thiếu; history bất biến.
9. TO/Traveler khác không đọc hoặc ghi được dữ liệu/ảnh; Admin không mặc định được quyền Staff.
10. Invoice không trùng, refund chỉ điều chỉnh hoa hồng một lần; báo cáo phân biệt sales/commission/cash và legacy/direct.
11. FE/Mobile cập nhật sau mutation, mở lại app/web thấy deadline/trạng thái server; không lộ khóa.
12. Tiền thật E2E chỉ chạy với tài khoản, giá trị và người nhận được chủ hệ thống cho phép riêng; không tự phát sinh giao dịch trả tiền khi duyệt tài liệu.
13. Staff không resolve; Admin có quyền riêng xét Disputed, đủ căn cứ mới Completed/AdminResolved; chưa đủ giữ Disputed, chuyển lỗi yêu cầu khắc phục. Thiếu lý do/căn cứ hoặc sai version không được ghi quyết định; concurrency không nhân đôi refund/commission.

## 13. Quyết định bổ sung đã duyệt ngày 25/11/2026

Mục này thay thế danh sách quyết định còn mở trước đây. Người dùng đã chốt cả tám đề xuất trong chat; không yêu cầu duyệt lại các quy tắc dưới đây.

1. **Hạn TO chuyển:** 72 giờ liên tục từ khi amount đã chốt và recipient đủ điều kiện, gồm cuối tuần/lễ. Quá hạn nhắc, gắn cờ và tạm dừng nhận booking mới; vẫn xử lý booking/refund cũ. Không dùng 3 ngày làm việc hoặc lịch lễ để tính hạn này.
2. **VND:** decimal, round half up tới đồng. Snapshot rate/policy tại booking; base từ payment xác minh. Làm tròn từng booking rồi cộng kỳ; refund tính lại commission mục tiêu và ghi chênh lệch với số đã hạch toán, không làm tròn độc lập từng khoản hoàn gây lệch.
3. **Evidence:** PDF/JPG/JPEG/PNG, tối đa 5 MB/file, 1–3 file/report, kiểm tra nội dung thực. Private, không sửa/xóa bản đã nộp. Chủ TO/Traveler, Staff và Admin có quyền xử lý hồ sơ được xem. Lưu 24 tháng sau khi đóng; hồ sơ tranh chấp không tự xóa. Đây là retention sản phẩm đồ án, không khẳng định thời hạn pháp lý cho production.
4. **Khiếu nại hết lượt:** chuyển `AwaitingAdminReview`; Admin có quyền riêng xét hợp đồng/chính sách snapshot và bằng chứng, quyết định kết quả và số tiền hoàn. Có lý do/căn cứ, không tùy ý đặt mức hoàn. Chưa đủ dữ kiện yêu cầu bổ sung, chưa tạo nghĩa vụ hoàn chưa chốt. Staff chỉ theo dõi. Quyền này khác quyết định đã nhận tiền của refund Disputed; không reset giới hạn 2 đề xuất TO/1 counter-offer Traveler.
5. **Chuyển bổ sung:** khi Admin xác định phần thiếu, lưu quyết định và TO có 72 giờ liên tục để chuyển phần thiếu; liên kết transfer mới. Không reset báo chuyển cũ và không tự mở lại cửa sổ im lặng 72h sau phản đối. Traveler nhận đủ hoặc Admin xác minh đã nhận đủ thì Completed; history giữ nguyên.
6. **Phản đối muộn:** khách gửi yêu cầu xem xét trong 7 ngày liên tục sau auto-completion. Hồ sơ review liên kết refund cũ; không tự đảo trạng thái/ledger. Admin xét bằng chứng; điều chỉnh bằng event/entry liên kết sau quyết định, không xóa lịch sử hoặc hoàn/đảo hoa hồng trùng. Hạn nội bộ này không mô tả là chấm dứt mọi quyền khiếu nại theo pháp luật.
7. **Kênh payOS:** kỹ thuật API/webhook và chủ thể phải được đối chiếu; Admin duyệt kênh khi API không đủ xác minh. Một TO có một kênh thu active, không dùng chung kênh cho hai TO độc lập. Đổi kênh cần xác minh lại; chỉ payment mới dùng phiên bản mới, payment/refund cũ giữ liên kết bất biến. Không giả định có API xác minh mà nhà cung cấp chưa hỗ trợ; không thêm mô hình nhiều nhân viên TO trong scope.
8. **Công nợ/credit:** hạn trả invoice 7 ngày liên tục từ server issuedAt (cùng giờ ngày thứ 7); quá hạn tạm dừng nhận booking mới, mở lại sau khi thanh toán đủ được xác minh. Credit bù khoản còn nợ trước, dư chuyển kỳ sau. Khi đóng TO, đối soát cuối rồi hoàn credit dư bằng chuyển khoản có chứng từ và xác nhận nhận tiền. Không tự trích nợ ngân hàng hoặc coi credit là tiền refund của khách.

### Phạm vi API bổ sung cần khóa trong phase contract

- Admin complaint review: list/detail/decision cho `AwaitingAdminReview`, quyền riêng, reason/evidence/version/operationId. Kết quả đủ căn cứ có thể tạo nghĩa vụ hoàn qua handoff idempotent.
- Traveler late-review request và Admin late-review decision liên kết refund đã auto-complete; kiểm tra cửa sổ 7 ngày phía server.
- Admin verify payment channel, invoice overdue sales gate và credit final settlement; không dùng endpoint Staff để thực hiện.
- Mọi quyết định tài chính/hoàn tất phải transaction + audit + notification và chống lặp, giữ dữ liệu legacy tách biệt.

Đây là duyệt nghiệp vụ; chưa triển khai, chưa cho phép migration production hay chuyển tiền thật. Khả năng nhiều merchant, bằng chứng chủ kênh và provider E2E vẫn cần kiểm chứng kỹ thuật trong plan. Các giới hạn chuỗi/DTO chi tiết phải khóa bằng convention hiện có trong phase API contract; không tự thêm quy tắc sản phẩm mới.
