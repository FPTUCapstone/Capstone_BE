# UC-70 Domain Glossary

## Operator Emergency Override

Luồng duy nhất được phép hủy một tour chưa khởi hành hoặc dừng một tour đang chạy. Tour Operator
phải gửi lệnh qua endpoint Emergency, cung cấp lý do và bằng chứng cần thiết. Mọi operation UC-70
đều có `ActorUserId` của Operator; không tồn tại System actor cho cancellation.

## Weather Safety Review

Weather provider có thể gửi webhook hoặc được reconciliation lấy lại. Backend xác minh, lưu durable
inbox, normalize event, đối chiếu meeting point/required stop và tạo review cho Operator. Review có thể
được escalated nếu quá SLA, nhưng không được thay đổi trạng thái Tour, Booking hoặc TripSession.

## Active-Trip Safety Hold

Tín hiệu khẩn cấp cho tour đang chạy. Hệ thống tạo review và hướng Operator xử lý ngay. Chỉ sau khi
Operator xác nhận và gọi Operator Emergency Override thì tour mới có thể chuyển sang
`EmergencyStopped`.

## Normalized Weather Event

Contract nội bộ độc lập provider, gồm event identity, authority, vùng ảnh hưởng hoặc Location id của
provider, effective time, risk level, directive và withdrawal state. Contract này chỉ phục vụ đánh giá và cung cấp bằng chứng
cho Operator, không phải lệnh hủy tour.

## Real-Time Alert Delivery

Tomorrow.io webhook là luồng nhận event chính. Backend xác minh `X-Signature` (HMAC-SHA256 trên chuỗi
timestamp), lưu durable inbox và trả `2xx` nhanh. `GET /v4/notifications?alertIds=` chỉ reconciliation
định kỳ để khôi phục webhook bị mất. Chỉ notification của `TomorrowIo:AlertId` được dùng.
`TomorrowIo:Enabled` là công tắc bật/tắt ingestion; không có Shadow/Automatic mode.

## Monitored Weather Location

Location được tạo theo meeting point hoặc stop có `IsRequiredForWeather = true`, không theo Traveler GPS,
và phải được link vào Alert mới sinh notification. Nhiều tour có thể dùng chung một location.

## Manual-Only Invariant

Weather ingestion và evaluation không được:

- đổi trạng thái Tour, TourSchedule, Booking hoặc TripSession;
- tạo `EmergencyTourCancellationOperation`;
- tạo refund, tính tiền hoàn hoặc payout.

## UC-70 Boundary

UC-70 chỉ cập nhật TourSchedule, Booking, TripSession và lưu cancellation context. Việc thanh toán và hoàn
tiền được xử lý riêng theo chính sách hủy booking áp dụng: UC-42 sở hữu refund, UC-71 sở hữu payout và tự
đọc trạng thái booking. UC-70 không gọi và không thay đổi hai module này.

## Public Weather Forecast

Dự báo thời tiết chỉ để xem, cho bất kỳ địa điểm nào trong Việt Nam, kể cả Guest chưa đăng nhập. Nó không
gắn với Tour, Booking hay Itinerary và không phải cảnh báo: không tạo Weather Safety Review, không gửi
thông báo, không ảnh hưởng quyết định của Tour Operator. Nguồn dữ liệu và quota tách riêng khỏi
Real-Time Alert Delivery, để việc nhiều người xem dự báo không bao giờ làm cảnh báo tour bị chậm hoặc mất.
_Avoid_: weather alert, weather warning (những từ đó chỉ dành cho UC-66/UC-14).

## Upcoming Trip Weather

Phần cá nhân của trang dự báo, chỉ cho Traveler đã đăng nhập: các Tour Departure mà Traveler có Booking đã
xác nhận, bắt đầu trong 5 ngày tới hoặc đang diễn ra, mỗi chuyến kèm Public Weather Forecast tại điểm hẹn
vào ngày khởi hành. Nó không tự đánh giá rủi ro: banner cảnh báo chỉ nhắc lại Weather Safety Review đang mở
của UC-66 và trạng thái xử lý của Tour Operator; quyết định vẫn thuộc Tour Operator.
_Avoid_: trip alert (đó là UC-14 cho lịch trình CSP), "chuyến đi" để chỉ Itinerary.
