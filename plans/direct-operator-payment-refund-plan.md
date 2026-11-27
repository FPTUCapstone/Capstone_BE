# Plan BE — Direct operator payment / refund / commission

Ngày 25/11/2026. **Đã ghi nhận duyệt nghiệp vụ ngày 25/11/2026; chưa thực thi.** Nguồn: `../specs/direct-operator-payment-refund-spec.md`.
Các đường dẫn không tồn tại là file đề xuất; không khẳng định đã tích hợp. Mỗi task phải được duyệt, test Red → Green → review spec rồi review code theo AGENTS.md.

## Phase 0 — Khóa nghiệp vụ và contract

1. Đối chiếu SRS/UC-28, cancellation, UC-42/refund, UC-44/45, payout, complaint/emergency; lập bảng rule cũ → mới trong `docs/direct-payment-cutover.md`. Làm rõ xung đột tên UC-67 và các quyết định mục 13. Done: owner duyệt, không có policy bị tự suy diễn.
2. Kiểm chứng payOS nhiều TO: `src/TripMate.Infrastructure/Services/Payment/PayOsPaymentGateway.cs`, `PayOsWebhookRegistrar.cs`, Application `Features/Traveler/Payments/Common/IPaymentGateway.cs`. Chỉ đọc/kiểm tra API không chuyển tiền; giao dịch thật phải có cho phép riêng về tài khoản/số tiền. Done: có bằng chứng channel/beneficiary/webhook/key rotation và giới hạn provider; không đoán API merchant delegation.
3. Khóa request/response/route/error/OpenAPI vào `docs/direct-payment-api-contract.md`, gồm DTO version, allowedActions, 72h boundary và evidence limits. Đối chiếu FE/Mobile trước triển khai. Done: client không còn contract giả định.

## Phase 1 — Baseline và schema (không thay flow đang chạy)

4. Ghi baseline build/unit/SQL/Redis hiện có; xử lý hoặc báo blocker hạ tầng, không reset Docker/DB. Verify `dotnet build TripMate.slnx`, `dotnet test TripMate.slnx` theo cấu hình test repo. Done: baseline có kết quả thật; không coi test bị chặn là pass.
5. Viết test migration idempotence/legacy-preservation trước; thêm `database/migrations/20261125_direct_operator_payment_refund_foundation.sql` và mappings. File domain dự kiến `PaymentFlow.cs`, `OperatorPaymentChannel.cs`, `DirectRefundCase.cs`, `DirectRefundTransfer.cs`, `CommissionStatement.cs` dưới `src/TripMate.Domain/{Enums,Entities}/`; mappings dưới Infrastructure/Persistence/Configurations. Done: migrate hai lần không đổi dữ liệu; legacy nguyên vẹn; index/unique/UTC/concurrency đủ. Không EF migration.
6. Backup/verify restore riêng, dry-run/apply local theo quy trình hiện có sau khi DB khỏe. Done: lưu bằng chứng số record và tổng tài chính trước/sau; không áp production trong task viết spec.

## Phase 2 — Kênh và payment thật

7. Test credential protection/ownership/verification states; tạo Application `Features/Operator/PaymentChannels/`, Infrastructure payment credential protector/storage, `OperatorPaymentChannelController.cs`. Done: secrets write-only, encrypted, log redacted; không Ready chỉ vì client tự khai.
8. Test snapshot/purpose/idempotent link/timeout recovery; sửa `Features/Traveler/Payments/Create/CreateOrReusePaymentLinkCommandHandler.cs`, `Common/IPaymentGateway.cs`, gateway/resolver và payment mapping. Done: owner chọn server-side; channel cũ bất biến; 15 phút UTC thực; không fallback.
9. Test wrong signature/channel/amount, duplicate/reordered webhook và late payment; sửa `PayOsWebhookController.cs`, `Features/Traveler/Payments/Webhook/ProcessPayOsWebhookCommand.cs`, `Common/PaymentCompletionProcessor.cs`; thêm route channel tương thích. Done: persist trước ack, paid/vé một lần, bất thường đi reconciliation, endpoint legacy còn chạy.
10. Test mở bán khi kênh chưa sẵn sàng và kênh mất kết nối; sửa entry point publish/bookability hiện có đã inventory ở phase 0. Done: chặn bán mới theo policy, vẫn xử lý đơn/refund cũ.

## Phase 3 — Refund trực tiếp

11. Test nguồn hủy/complaint/emergency và cap đồng thời; thêm Application `Features/Bookings/DirectRefunds/Create/`, cập nhật các handoff `Features/Bookings/`, `Features/Operator/Tours/Emergency/Common/EmergencyRefundHandoff.cs`, `Features/Complaints/PartialRefundProposals/`. Done: flow-based dispatch, một nghĩa vụ/source, giữ giới hạn thương lượng; direct không bị worker cũ xử lý.
12. Test recipient validate/ownership/version/snapshot; thêm `DirectRefunds/Recipient/` và Traveler API. Done: amount không bị client đổi, recipient audit, deadline theo policy đã chốt.
13. Test private upload/download/security/idempotent report; thêm `DirectRefunds/Transfers/`, evidence port/storage, `OperatorDirectRefundsController.cs`. Done: không persist report thiếu bằng chứng, 72h từ server, retry không reset, failure không hiện success.
14. Test `DirectRefunds/Responses/`: nhận đủ, chưa nhận, thiếu tiền, remaining amount; thêm Traveler mutations và Staff read-only controller. Done: dispute đóng auto-completion; Staff không có endpoint resolve/complete; Traveler nhận đủ có thể hoàn tất sau phản đối. Hạn chuyển bổ sung 72h từ quyết định Admin, không tự mở lại cửa sổ im lặng sau tranh chấp.
14a. Thêm `DirectRefunds/Disputes/` queries/resolve, `AdminDirectRefundDisputesController.cs` và policy riêng. Test Admin quyền đọc evidence có phạm vi; Staff bị chặn mutation; kết quả đủ/chưa đủ/chuyển lỗi; reason/evidence/version/idempotency và race với Traveler confirm. Done: Completed dùng AdminResolved có audit/notification, không yêu cầu chuyển trùng hoặc kết tội khách tự động; các nhánh chưa hoàn không ghi credit như Completed.
15. Test clock trước hạn/đúng hạn/sau hạn, job vs dispute race, restart, multi-instance; thêm `DirectRefundDeadlineProcessor.cs` và `Infrastructure/Services/DirectRefundDeadlineBackgroundService.cs`. Done: atomic CAS/transaction cùng mutation, completion method đúng, exactly-once ledger effects, eventual sweep sau downtime.
16. Test durable notifications/reminder dedup và thông tin nhạy cảm; thêm adapters vào notification mechanism hiện có. Done: báo chuyển/nhắc/hoàn tất có deep link; mail lỗi không rollback nghĩa vụ hoặc đổi deadline.

## Phase 4 — Hoa hồng và báo cáo

17. Test eligible base/rate snapshot/rounding/pending refund/late credit; thêm `Features/Operator/CommissionStatements/`, `Features/Admin/CommissionStatements/`, financial ledger projections. Done: không lẫn payout legacy; invoice bất biến, credit một lần.
18. Test trả invoice qua payOS TripMate; reuse payment infrastructure với purpose tách biệt, không dùng khóa TO để thu hoa hồng. Done: webhook tour không thanh toán nhầm invoice và ngược lại; over/underpayment đi đối soát.
19. Test mixed-flow report/export/dashboard; sửa `Features/Operator/Revenue/`, các admin reporting/payout readers đã xác định, và worker legacy filters. Done: sales, commission owed, cash received khác nhãn; snapshot lịch sử không bị ghi đè.

## Phase 5 — Cutover và nghiệm thu

20. Test legacy/direct coexistence và rollback flag, cập nhật `README.md`, `.env.example`, `docs/direct-payment-cutover.md`; không thêm secret thật. Done: tắt tạo direct mới vẫn drain pending direct, không tự chuyển model.
21. Chạy focused domain/Application tests mỗi task; SQL integration cho transaction/unique/migration/concurrency; Redis tests nếu đường đi sử dụng Redis. Cuối cùng `dotnet build TripMate.slnx`, `dotnet test TripMate.slnx` và formatting đúng CI. Done: test failure không bị skip để qua gate.
22. E2E BE–FE–Mobile: hai TO/kênh thật, đúng người nhận, paid, hết hạn, refund accept/dispute/auto72h, Staff chỉ xem, invoice/credit, legacy. Thử 72h bằng clock kiểm soát ở test riêng, không sửa clock production/payOS thật. Done: bằng chứng giao dịch có che thông tin, giới hạn tiền thật được cho phép và xác nhận hoàn trả test.

## Điều kiện phát hành

- SRS/change request, các policy còn mở và ba spec/plan được duyệt.
- API security, secret recovery/rotation, backup restore, webhook HTTPS, private evidence delivery verified.
- Không bật mô hình direct chỉ vì thu tiền đã chạy: refund, tranh chấp, tự hoàn 72h, hoa hồng và giám sát phải hoạt động.
- Không xóa workers/bảng legacy trước khi mọi nghĩa vụ legacy đã kết thúc và có phê duyệt riêng.
- Không commit/push/PR/deploy theo plan này khi chưa được yêu cầu.

## Cập nhật duyệt cuối ngày 25/11/2026

Đã duyệt tám quyết định tại mục 13 spec BE cùng tên. Phần này ưu tiên hơn mọi câu “còn mở/chờ duyệt” cũ nói về tám quyết định đó; không mặc định đã viết hoặc kiểm chứng code.

- Hạn TO chuyển ban đầu và chuyển bổ sung sau quyết định Admin: **72 giờ liên tục**, gồm cuối tuần/lễ. Không còn dùng 3 ngày làm việc. Hạn Traveler phản hồi ban đầu là một cửa sổ 72h riêng.
- Hoa hồng decimal round half up theo booking, rate chụp tại booking, payment xác minh làm base; refund điều chỉnh chênh lệch commission mục tiêu.
- Evidence PDF/JPG/JPEG/PNG, tối đa 5 MB/file, 1–3 file/report, private; retention 24 tháng sau đóng, giữ hồ sơ đang tranh chấp. Admin xử lý hồ sơ có quyền xem; không public evidence.
- Hết lượt thương lượng khiếu nại: `AwaitingAdminReview`, Admin xét và quyết định kết quả/amount theo hợp đồng, chính sách snapshot và bằng chứng. Staff vẫn chỉ theo dõi. Chưa đủ dữ kiện thì yêu cầu bổ sung, không tạo amount giả.
- Sau tranh chấp nhận tiền, không reset 72h bằng báo chuyển bổ sung. Khách nhận đủ hoặc Admin xác minh đủ thì Complete; lưu AdminResolved khi phù hợp.
- Khách được gửi late-review trong 7 ngày sau auto-complete. Admin xem hồ sơ liên kết, không đảo lịch sử/ledger tự động; đây là hạn hỗ trợ nội bộ, không xóa quyền bên ngoài.
- Kênh TO: một kênh active/TO, không chung cho hai TO; Admin đối chiếu/duyệt khi API không đủ xác minh; thay kênh chỉ áp giao dịch mới, giữ kênh cũ cho giao dịch đang có.
- Hoa hồng hạn 7 ngày liên tục từ issuedAt; quá hạn dừng booking mới, vẫn xử lý đơn/refund cũ. Credit bù nợ, dư chuyển kỳ; đóng tài khoản phải đối soát và hoàn credit dư có chứng từ/xác nhận.

### Task bổ sung bắt buộc trước nghiệm thu

- Khóa contract cho Admin complaint decision, Traveler/Admin late review, channel approval và credit final settlement; không gộp vào quyền Staff read-only.
- Test Admin complaint decision tạo refund idempotent, chưa đủ bằng chứng không chốt tiền; late-review trước/đúng/sau hạn 7 ngày và điều chỉnh không trùng.
- Test 72h ban đầu/bổ sung và 7 ngày invoice trên server, cuối tuần/lễ không kéo dài; booking gate vẫn cho xử lý nghĩa vụ cũ.
- Test rounding theo booking, refund nhiều lần, credit vượt nợ và đóng TO; ghi history/adjustment bất biến.
- FE bổ sung screen/actions Admin trên Web trong task screen contract; Mobile chỉ TO/Traveler xem kết quả, bổ sung bằng chứng và gửi late review, không làm Admin Flutter.
