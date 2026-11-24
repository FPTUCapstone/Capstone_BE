# Operator sign-up: hợp đồng đối tác và chữ ký online
Trạng thái: Được người dùng cho triển khai ngày 24/11/2026, theo các quyết định kiểm thử ở mục 12. Ngày khởi tạo: 23/11/2026.
Nhánh: fix/linhnv-operator-sign-up (BE, FE, Mobile).

## 1. Nguồn và phạm vi
Quyết định người dùng: Staff duyệt hồ sơ -> tài khoản chờ ký; TO đăng nhập thấy hợp đồng; ký và lưu thành công -> kích hoạt. Không có bước TripMate ký đối ứng tiếp theo.
SRS_rev10 (5).docx: UC-02 đăng ký TO, UC-03 gửi lại hồ sơ, UC-49 duyệt hồ sơ, BR-07/BR09. SRS hiện chỉ yêu cầu checkbox Terms/Privacy/Partner Agreement và duyệt trước khi vào workspace. Bước ký là thay đổi đã chốt trong chat, cần cập nhật SRS; không tự gán mã UC/BR/MSG mới.
Ngày 24/11/2026 người dùng đã yêu cầu chuyển sang triển khai hiển thị hợp đồng cho TO và vẽ chữ ký. Phần thu nét ký và kiểm chứng kỹ thuật có thể thực hiện; OTP và chính sách TO cũ đang được hỏi, chưa bật gate/kích hoạt phụ thuộc các quyết định này. Không gộp chỉnh sửa UI/UX đang có thành thay đổi nghiệp vụ hợp đồng.
Không tuyên bố vẽ chữ ký/OTP có giá trị tương đương chữ ký số được chứng thực.

## 2. Hiện trạng đã kiểm tra
- RegisterOperatorCommand.AcceptTerms bắt buộc; tạo Users.PendingApproval và OperatorProfiles.PendingApproval.
- Staff là người được phép duyệt trong ApproveOperatorApplicationCommandHandler, không phải Administrator.
- Duyệt hiện ghi Users.Active + OperatorProfiles.Approved ngay.
- AuthResponseDto.ApplicationStatus, Web session và Mobile route guard hiện dùng trạng thái hồ sơ để cho vào workspace; Approved một mình sẽ không còn đủ.
- Chưa có thực thể hợp đồng, lưu phiên bản tài liệu, bằng chứng chữ ký hay màn hình ký.

## 3. Luồng được chốt
1. Guest gửi đăng ký và giấy phép, tích điều khoản như hiện tại. Không bắt ký trước nút đăng ký.
2. TO xác minh email theo luồng hiện có, đăng nhập được để xem trạng thái; không vào nghiệp vụ kinh doanh.
3. Staff mở hồ sơ, kiểm tra thông tin và người đại diện. Hồ sơ thiếu/sai: giữ nhánh từ chối/gửi lại hiện có.
4. Staff bấm duyệt: lưu kết quả duyệt + tạo duy nhất một hợp đồng từ phiên bản mẫu đã công bố và snapshot hồ sơ. Tài khoản chuyển PendingContractSignature, hồ sơ Approved. Không trả thông báo Account activated.
5. TO đăng nhập/refresh hoặc quay lại app: BE trả onboardingState=AwaitingSignature, chuyển tới màn hình hợp đồng.
6. Hiển thị nội dung đầy đủ, thông tin người ký, phiên bản; cho tải bản chưa ký. TO xác nhận đã đọc và ký bằng phương thức được duyệt ở mục 12.
7. BE xác minh phiên/người ký, đúng hợp đồng/phiên bản/hash, trạng thái và bằng chứng. Lưu hoàn tất bản đã ký, sau đó transaction chuyển hợp đồng Signed và Users.Active.
8. Client refresh session qua cơ chế hiện có rồi vào dashboard. Có thể xem/tải hợp đồng đã ký trong hồ sơ.
9. Mất mạng/thoát giữa chừng: vẫn chờ ký, mở lại đọc trạng thái thật. Nhấn nhiều lần không tạo nhiều hợp đồng hoặc kích hoạt nhiều lần.

## 4. Trạng thái và quyền
Tách ApprovalStatus (PendingApproval/Approved/Rejected) khỏi AccountStatus thêm PendingContractSignature và ContractStatus (PendingSignature/Signed/Voided). Không dùng Locked làm chờ ký vì Locked là trạng thái bảo mật/nghiệp vụ khác.
OnboardingState BE tính: AwaitingReview, Rejected, AwaitingSignature, Active, Unresolved. Client không tự suy luận Approved=Active.
Điều kiện TO kinh doanh: đúng role + email đã xác minh theo chính sách hiện tại + tài khoản Active + hồ sơ Approved + hợp đồng Signed hoặc legacy exemption đã duyệt.
PendingContractSignature chỉ được auth/refresh/logout, xem hồ sơ trạng thái, đọc/tải/ký hợp đồng của mình và hỗ trợ. Mọi API kinh doanh phải chặn tại BE; deep link/client cũ/JWT cũ không được bỏ qua. Kiểm tra trạng thái persisted, không chỉ claims.
Nếu tài khoản Locked/Inactive khi đang ký: không tự mở khóa/kích hoạt.
Sau ký cần cập nhật session/claims theo hạ tầng session_version hiện có, không tạo cơ chế token song song.
Chưa áp dụng cho Traveler/Staff/Administrator/ServiceProvider.

## 5. Nội dung và dữ liệu đề xuất
- OperatorContractTemplates: id, version, title, body/storage_key, content_sha256, published_at_utc, published_by, status. Mẫu đã sử dụng bất biến, không ghi đè.
- OperatorContracts: id, operator_user_id, application/review identity, template_id/version, immutable legal snapshot, content_sha256, unsigned_document_key, signed_document_key, status, approved_by/at, signed_at, signer_name/title, signature_method, signing_attempt_id, row_version.
- ContractSigningOperations: idempotency_key, contract_id/operator_user_id, accepted_hash/version, status/result và evidence reference; unique theo owner+key.
- Khóa phát hành duy nhất theo application/review identity + contract generation; một generation chỉ có một hợp đồng. Khi void và phát hành lại phải tạo generation mới, không tái dùng khóa cũ.
- Lưu riêng accepted_content_sha256 (nội dung/snapshot TO đồng ý), unsigned_document_sha256 và signed_document_sha256. Không so hash PDF đã thêm chữ ký với hash nội dung trước ký như thể chúng phải giống nhau; signed artifact phải tham chiếu đúng accepted content/version.
- Bằng chứng ký: method, full-name/authority snapshot, consent version, server UTC timestamp, signature file hash/storage key, verification reference nếu OTP/provider; IP/user-agent chỉ nếu có mục đích, giới hạn và policy đã duyệt. Không ghi OTP/token vào audit.
- Snapshot doanh nghiệp: tên pháp lý, MST, địa chỉ, giấy phép, đại diện/chức danh/căn cứ ủy quyền, email liên hệ. Tài khoản ngân hàng chỉ nếu mẫu hợp đồng yêu cầu; dùng nguồn profile đã xác minh, không tự sửa dữ liệu thanh toán.
- Cần bổ sung/kiểm tra trường đại diện hiện có: contactPerson chưa đủ để khẳng định đại diện pháp luật. Không tự nhận đó là người có thẩm quyền.
- Ngày hiển thị dd/MM/yyyy; giờ cần thiết giữ rõ timezone, server/API lưu UTC ISO. Mã và số tiền không thay đổi cách tính.
Tên bảng/kiểu/độ dài cuối cùng chốt sau khảo sát mapping; migration SQL idempotent database-first, không EF migration.

## 6. API đề xuất (chưa tồn tại)
- GET /api/v1/operator/contracts/current: metadata, status, full display content/version/hash, signer snapshot, canSign, onboardingState. Không trả storage key nội bộ.
- GET /api/v1/operator/contracts/{id}/document: stream private bản chưa ký/đã ký theo trạng thái; owner hoặc Staff được phân quyền. Không public URL lâu dài.
- POST /api/v1/operator/contracts/{id}/sign: idempotency key, expectedVersion/hash, signer confirmation, acceptedTerms=true, bằng chứng theo method. Dạng multipart cho chữ ký vẽ nếu chọn; không nhận operatorId/activationStatus từ client.
- Nếu chọn OTP: POST .../{id}/signing-challenges và challenge confirmation trong sign; TTL/attempt/rate-limit theo quyết định12, challenge ràng buộc owner + contract + hash + purpose. Không tự dùng OTP đặt lại mật khẩu.
- Session/login/refresh + application status DTO bổ sung onboardingState/contractStatus/currentContractId cho đúng TO; Web BFF không lộ refresh token.
- Staff approval response: hồ sơ Approved, account PendingContractSignature, contract reference; repeat/concurrent approval không sinh bản trùng.
Lỗi dùng Result/ProblemDetails với mã ngữ nghĩa: contract.not_found(404), forbidden(403), version_changed/state_conflict(409), validation(400), signing_unavailable(503). OTP errors chỉ chốt nếu chọn OTP. UI dịch lỗi thân thiện, không hiện mã thô.

## 7. Toàn vẹn và đồng thời
- Duyệt và tạo contract snapshot cùng DB transaction; mẫu không khả dụng -> không duyệt nửa chừng.
- Không giữ DB transaction trong lúc upload/gọi provider/PDF. Stage private artifact, kiểm tra hash trước; transaction cuối conditional update PendingSignature+account PendingContractSignature+profile Approved.
- Chỉ báo hoàn tất/kích hoạt khi signed artifact đã durable và metadata commit; lỗi upload/verification -> không Active.
- Idempotency duplicate trả cùng kết quả; khóa duy nhất+row version ngăn nhiều chữ ký. Request cùng key khác payload ->409.
- Nếu ký provider bất đồng bộ: chỉ webhook đã xác thực/dedupe được hoàn tất, không tin redirect phía client.
- Bản đã ký bất biến. Thay thông tin pháp lý/mẫu khi chưa ký -> void bản cũ, phát hành bản mới, client cũ409. Không thêm màn hình sửa hợp đồng tùy tiện.
- Lỗi trước commit dọn staging/orphan bằng cơ chế cleanup; commit thành công nhưng mất response thì GET/retry xác nhận Signed.
- Notification/audit không được báo thành công trước commit; dùng cơ chế durable có sẵn, gửi lại không ký/kích hoạt lại.

## 8. Web và Mobile
Web route đề xuất /partner/contract; hồ sơ /partner/profile có mục hợp đồng đã ký. Xác nhận tên route theo ROUTES hiện tại trước code.
Mobile /operator/contract và đường xem hợp đồng trong profile. Cubit/use cases/domain repository; không gọi network/upload trong widget.
Hợp đồng dài cuộn đọc, signer identity rõ, xác nhận đồng ý, vùng ký accessible có lựa chọn thay thế phù hợp, nút xóa ký/xem lại/gửi. Không ép cuộn cuối như bằng chứng đã hiểu.
Loading/empty/error/retry/upload/signing/success states. Back/logout không vượt guard. Khi đã ký chỉ xem/tải, không ký lại.
Web/mobile dùng chung một contract state; ký trên một thiết bị, thiết bị còn lại refresh chuyển đúng, không giữ form ký cũ.
OTP/notification/deep link quay lại đúng màn hình sau login, không làm lộ hợp đồng của owner khác.

## 9. Audit, bảo mật và lưu trữ
Audit StaffApprovedAwaitingSignature, ContractIssued, ContractSigned, AccountActivated, ContractVoided; actor/target/version/hash/time/result; không log ảnh chữ ký hay OTP.
Private storage, authorize mọi lần đọc; giới hạn file/type/bytes thật, chống payload hình lỗi, không public web root. Không nhúng script/HTML template chưa sanitize.
Retention/xóa bằng chứng và quyền truy cập Staff cần chính sách được duyệt; không hứa lưu vĩnh viễn hoặc xóa tùy ý.
Consent checkbox đăng ký hiện tại vẫn độc lập. Hợp đồng phải chỉ rõ đúng bên ký và điều khoản được chủ sản phẩm cung cấp, không AI tự soạn nghĩa vụ tài chính.

## 10. Migration và tương thích
Không đổi hàng loạt TO đang Active sang chờ ký mà chưa duyệt chính sách. Đề xuất rollout: TO Active+Approved hiện hữu được đánh dấu legacy exemption có audit mốc cutover; TO được duyệt sau cutover bắt buộc ký. Cần duyệt trước khi migrate.
Clients cũ không biết PendingContractSignature phải fail closed, không crash/đưa vào workspace.
Rà tất cả CHECK constraints status, enum/EF mapping, auth/login/google/refresh/session, effective status/provisioning, account-lock/unlock và admin filters; không chỉ sửa approve handler.
Deploy DB -> BE -> FE/Mobile tương thích; bật feature sau khi clients sẵn sàng. Rollback không xóa hợp đồng hoặc bằng chứng đã ký.
Trong cửa sổ cutover phải tạm dừng Staff duyệt hồ sơ từ trước khi chạy migration đến khi tất cả API/client tương thích đã được triển khai và bật signing. Miễn ký legacy chỉ được ghi một lần tại migration; hồ sơ được duyệt bằng code cũ/flag tắt sau mốc đó không được tự miễn ký. Trước khi mở lại chức năng duyệt, đối soát TO Active+Approved không có hợp đồng Signed và không có exemption, xử lý từng hồ sơ theo cutover đã duyệt; không bulk exempt TO mới hoặc tự sửa trạng thái. Nếu rollback tắt signing thì tiếp tục tạm dừng duyệt để tránh tái tạo khoảng hở.

## 11. Tiêu chí chấp nhận
- Staff duyệt đúng quyền -> một contract và chờ ký, chưa Active.
- Không ký, sai hash, sai owner, email chưa verified, Locked, upload/provider fail -> không kích hoạt.
- Ký hợp lệ -> một bản signed immutable, một activation, audit đầy đủ; GET/tải lại được.
- Duplicate/concurrent sign/approve và mất response không tạo bản trùng.
- Login, refresh, Google (nếu TO hiện hỗ trợ), app resume và deep link đều áp dụng guard persisted.
- Web+Mobile thông tin giống nhau, hỗ trợ màn hình nhỏ/bàn phím/loading/error; ngày đúng thứ tự.
- Hợp đồng bị void/version mới không ký được bằng màn hình cũ.
- TO legacy theo quyết định cutover; các role còn lại không regression.
- Không ảnh hưởng tiền/commission/refund/tour logic ngoài gate truy cập.

## 12. Quyết định cần duyệt trước triển khai
A. Đã chốt ngày 24/11/2026: vẽ chữ ký + họ tên + consent + OTP email chính chủ. Không gọi ảnh chữ ký là chữ ký số được chứng thực.
B. Người dùng đã chốt `deliverables/TripMate_Hop_dong_hop_tac_TO_MAU_KIEM_THU.docx` để test; Draft local 2 có đủ 11 biến và preview Ready. Đây chưa phải hợp đồng pháp lý chính thức. Users.FullName hiện là contactPerson, không phải dữ liệu đã xác minh về người đại diện; cần TO xác nhận họ tên/chức danh/tư cách ký trước khi đóng băng tài liệu riêng của TO. Không tự sửa dữ liệu ngân hàng hoặc nhận contactPerson là bằng chứng ủy quyền.
C. Tái dùng giá trị policy chuẩn hiện có: TTL 3 phút, tối đa 5 lần sai/challenge, resend 60 giây. Purpose/HMAC binding và state ký tách khỏi đặt lại mật khẩu. Giới hạn 10 challenge/15 phút theo tài khoản trong DB và theo IP tại API. Dùng đồng hồ thực cho TTL OTP/lease, không dùng đồng hồ giả lập chuyến đi. Chỉ giao email qua transport thực đã cấu hình; mã không được trả API hoặc log.
D. Đã chốt: giữ nguyên TO Active+Approved cũ; chỉ hồ sơ duyệt sau cutover phải ký. Migration ghi miễn ký một lần, gồm tài khoản bị khóa có trạng thái trước khóa là Active và hồ sơ Approved; không tự Active hoặc mở khóa tài khoản.
E. Cập nhật phạm vi theo yêu cầu tiếp theo: Admin quản lý mẫu DOCX, validation, preview PDF và công bố phiên bản trước luồng TO ký. Xem admin-contract-template-management-spec.md và plan tương ứng. Thay thế phương án chỉ triển khai mẫu có kiểm soát, không có màn hình quản trị. Đây không phải CMS tự do; engine/private storage/vùng ký phải được xác minh trước lựa chọn.
F. Hạn ký/retention: đề xuất phase 1 không tự hết hạn hợp đồng; không tự khóa vì chưa ký, chỉ giữ chờ ký; retention chủ sản phẩm quyết định.
Spec chưa phê duyệt không phải lệnh triển khai.

## 13. Khảo sát kỹ thuật ngày 24/11/2026
- Auth hiện trả effective Status Active cho TO có hồ sơ pending/rejected để cho phép đăng nhập; cần onboardingState riêng, không suy ra trạng thái kinh doanh từ Active trên wire.
- UserSessionValidation chưa chặn toàn bộ nghiệp vụ TO bằng trạng thái persisted. Phải có policy chung và ngoại lệ contracts/application/auth/support; kiểm tra JWT cũ vẫn bắt buộc.
- PendingContractSignature dài 24 ký tự, vừa schema hiện có. Migration mới phải cập nhật CHECK của Users.status, Users.status_before_lock và UserUnlockOperations.restored_status. Không sửa migration cũ hoặc enum value cũ.
- Snapshot người ký cần hoàn thành trước khi TO đọc và chấp nhận PDF cuối. Chữ ký phải gắn vào PDF đó, không render lại từ DOCX sau khi TO đồng ý.
- Đã thử nghiệm ngoài product code việc mở PDF, gắn PNG giả, lưu và mở lại; giữ số trang và file nguồn nguyên vẹn. PDFsharp 6.2.4 Core có license MIT trong nuspec gói đã tải. Phiên bản mới 6.5 thay license nên không tự nâng major/minor; pin và review dependency. NuGet: https://www.nuget.org/packages/PDFsharp/6.2.4.
- Dùng override System.Security.Cryptography.Pkcs 10.0.14 đang có trong BE, không dùng transitive 8.0.1 có cảnh báo NuGet.
- Không dùng PreparePreviewDocxAsync cho hợp đồng riêng của TO: hàm hiện chèn watermark preview. Cần đường final riêng, snapshot đúng TO và vùng chữ ký có khoảng trống đủ; tọa độ lấy từ chính PDF cuối, không lấy từ PDF fake của Admin.
- Capture là PNG được kiểm tra bytes/giới hạn/pixel thực ở BE; không tin mime/filename hoặc coi ảnh không trống là bằng chứng danh tính. Không log ảnh/nét ký.
