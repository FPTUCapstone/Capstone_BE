/*
    UC-26 development/UAT seed: fixtures for the public "View Tour Details" page.

    MANUAL, DEVELOPMENT ONLY. It is not part of schema application, migrations or CI.
    It PUBLISHES fake tours, a fake operator and fake reviews, so it refuses to run unless the
    database name contains UAT, Dev, Test or Local. Never run it against a database that real travelers
    can reach.

    Run once, after schema v7 and all migrations (sqlcmd needs -I and, for Vietnamese text, -f 65001):
        sqlcmd -S <server> -d <UAT database> -I -f 65001 -b -i 20261017_seed_uc26_view_tour_details_development.sql
    Running it twice aborts with a message; recreate the database to reset (database/README.md).
    Fixture users use example.invalid addresses and have no password.

    Fixtures (ids are what the UAT script expects on a fresh database):
      1  Published, fully populated: 4 departures (registration closed, bookable, sold out, legacy price),
         3 active images + 1 deleted, a deactivated POI stop, 3 published reviews (4.7)
      2  Published, minimal: no media, departures, rating or terms
      3  Published, only departure sold out
      4-10  HIDDEN: draft, pending, rejected, inactive, approved-not-yet-published, unapproved operator,
         locked operator. The public API must answer 404 for all of them.
      9007199254740993  Published, large signed Int64 id
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() NOT LIKE N'%UAT%' AND DB_NAME() NOT LIKE N'%Dev%'
   AND DB_NAME() NOT LIKE N'%Test%' AND DB_NAME() NOT LIKE N'%Local%'
    THROW 51000, N'UC-26 seed refused: it publishes fake tours and reviews. Run it only on a throw-away development/UAT database whose name contains UAT, Dev, Test or Local.', 1;

IF EXISTS (SELECT 1 FROM dbo.Users WHERE email = N'uc26-approved@example.invalid')
    THROW 51000, N'UC-26 seed already applied to this database. Recreate the database to reset it.', 1;

BEGIN TRANSACTION;

-- Operators: approved, pending application, locked account
INSERT dbo.Users(role,status,email,full_name) VALUES
 ('TourOperator','Active',N'uc26-approved@example.invalid',N'Operator Approved'),
 ('TourOperator','Active',N'uc26-pending@example.invalid',N'Operator Pending'),
 ('TourOperator','Locked',N'uc26-locked@example.invalid',N'Operator Locked'),
 ('Traveler','Active',N'uc26-traveler@example.invalid',N'Traveler UAT');
DECLARE @opOk BIGINT = (SELECT user_id FROM dbo.Users WHERE email=N'uc26-approved@example.invalid');
DECLARE @opPending BIGINT = (SELECT user_id FROM dbo.Users WHERE email=N'uc26-pending@example.invalid');
DECLARE @opLocked BIGINT = (SELECT user_id FROM dbo.Users WHERE email=N'uc26-locked@example.invalid');
DECLARE @traveler BIGINT = (SELECT user_id FROM dbo.Users WHERE email=N'uc26-traveler@example.invalid');
INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,contact_phone,contact_address,approval_status) VALUES
 (@opOk,N'Hội An Heritage Tours',N'TAX-OK-0001',N'LIC-OK-0001',N'0905 123 456',N'12 Nguyễn Thái Học, Hội An (riêng tư)','Approved'),
 (@opPending,N'Pending Tours',N'TAX-PEND-0002',N'LIC-PEND-0002',N'0900 000 002',N'Private','PendingApproval'),
 (@opLocked,N'Locked Tours',N'TAX-LOCK-0003',N'LIC-LOCK-0003',N'0900 000 003',N'Private','Approved');

-- Catalogue
INSERT catalog.Destinations(name) VALUES (N'Đà Nẵng'),(N'Hội An'),(N'Huế');
DECLARE @dnId BIGINT = (SELECT destination_id FROM catalog.Destinations WHERE name=N'Đà Nẵng');
DECLARE @haId BIGINT = (SELECT destination_id FROM catalog.Destinations WHERE name=N'Hội An');
DECLARE @hueId BIGINT = (SELECT destination_id FROM catalog.Destinations WHERE name=N'Huế');
INSERT catalog.POICategories(name) VALUES (N'Di sản UAT');
DECLARE @cat INT = SCOPE_IDENTITY();
INSERT catalog.POIs(category_id,name,address,latitude,longitude,status) VALUES
 (@cat,N'Phố cổ Hội An',N'Trần Phú, Hội An',15.8801,108.3380,'Active'),
 (@cat,N'Chùa Cầu',N'Nguyễn Thị Minh Khai, Hội An',15.8774,108.3260,'Active'),
 (@cat,N'Bà Nà Hills',N'Hòa Ninh, Hòa Vang, Đà Nẵng',15.9954,107.9964,'Active'),
 (@cat,N'Chợ đêm đã đóng cửa',N'Đà Nẵng',16.0600,108.2200,'Inactive');
DECLARE @poi1 BIGINT = (SELECT poi_id FROM catalog.POIs WHERE name=N'Phố cổ Hội An');
DECLARE @poi2 BIGINT = (SELECT poi_id FROM catalog.POIs WHERE name=N'Chùa Cầu');
DECLARE @poi3 BIGINT = (SELECT poi_id FROM catalog.POIs WHERE name=N'Bà Nà Hills');
DECLARE @poiClosed BIGINT = (SELECT poi_id FROM catalog.POIs WHERE name=N'Chợ đêm đã đóng cửa');
DECLARE @policy BIGINT = (SELECT TOP (1) cancellation_policy_template_id FROM commerce.CancellationPolicyTemplates ORDER BY 1);

-- T1: rich published tour
INSERT commerce.Tours(operator_user_id,title,description,destination_summary,base_price,duration_days,status,published_at,inclusions,exclusions,cancellation_policy_template_id)
VALUES(@opOk,N'Hội An – Đà Nẵng 2N1Đ: ẩm thực và di sản',
 N'Hai ngày khám phá phố cổ Hội An về đêm, thưởng thức ẩm thực địa phương và lên Bà Nà Hills.' + CHAR(10) + N'Phù hợp gia đình và nhóm bạn.',
 N'Đà Nẵng, Hội An',1500000,2,'Published',DATEADD(DAY,-5,SYSUTCDATETIME()),
 N'Xe đưa đón khứ hồi' + CHAR(10) + N'- Khách sạn 3 sao (1 đêm)' + CHAR(13)+CHAR(10) + N'• Hướng dẫn viên tiếng Việt' + CHAR(10) + N'Vé cáp treo Bà Nà',
 N'Chi phí cá nhân' + CHAR(10) + N'Đồ uống ngoài chương trình',@policy);
DECLARE @t1 BIGINT = SCOPE_IDENTITY();
INSERT commerce.TourDestinations(tour_id,destination_id,sequence_no) VALUES(@t1,@dnId,1),(@t1,@haId,2);
INSERT commerce.TourCancellationPolicyRules(tour_id,days_before_departure,cancellation_fee_percent)
 SELECT @t1,days_before_departure,cancellation_fee_percent FROM commerce.CancellationPolicyTemplateRules WHERE cancellation_policy_template_id=@policy;
INSERT commerce.TourItineraryItems(tour_id,day_no,sequence_no,poi_id,planned_time,stay_duration_minutes,notes) VALUES
 (@t1,1,1,@poi3,'08:30',180,N'Có hướng dẫn viên đi cùng'),
 (@t1,1,2,@poiClosed,NULL,45,NULL),
 (@t1,2,1,@poi1,'09:00',120,N'Ăn sáng tại chợ'),
 (@t1,2,2,@poi2,'11:30',40,NULL);
INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,meeting_point,total_capacity,reserved_capacity,status,registration_deadline_at,adult_price,child_price,infant_price) VALUES
 (@t1,DATEADD(DAY,20,SYSUTCDATETIME()),DATEADD(DAY,22,SYSUTCDATETIME()),N'Sảnh khách sạn Mường Thanh, Đà Nẵng',12,4,'Scheduled',DATEADD(DAY,18,SYSUTCDATETIME()),1500000,900000,0),
 (@t1,DATEADD(DAY,30,SYSUTCDATETIME()),DATEADD(DAY,32,SYSUTCDATETIME()),N'Cổng Chợ Hàn',10,10,'Scheduled',NULL,1600000,NULL,NULL),
 (@t1,DATEADD(DAY,3,SYSUTCDATETIME()),DATEADD(DAY,5,SYSUTCDATETIME()),N'Ga Đà Nẵng',10,1,'Scheduled',DATEADD(DAY,-1,SYSUTCDATETIME()),1400000,800000,0),
 (@t1,DATEADD(DAY,45,SYSUTCDATETIME()),DATEADD(DAY,47,SYSUTCDATETIME()),NULL,8,0,'Scheduled',NULL,NULL,NULL,NULL),
 (@t1,DATEADD(DAY,25,SYSUTCDATETIME()),DATEADD(DAY,27,SYSUTCDATETIME()),N'Đã hủy',8,0,'Cancelled',NULL,1500000,NULL,NULL),
 (@t1,DATEADD(DAY,-10,SYSUTCDATETIME()),DATEADD(DAY,-8,SYSUTCDATETIME()),N'Đã qua',8,3,'Completed',NULL,1500000,NULL,NULL);
INSERT commerce.TourMedia(tour_id,cloudinary_public_id,delivery_url,caption,sort_order,is_primary,lifecycle_status,created_at,updated_at,alt_text) VALUES
 (@t1,N'uat/t1-b',N'https://picsum.photos/seed/hoian-b/1200/675',NULL,2,0,'Active',SYSUTCDATETIME(),SYSUTCDATETIME(),N'Đèn lồng Hội An'),
 (@t1,N'uat/t1-a',N'https://picsum.photos/seed/hoian-a/1200/675',N'Phố cổ về đêm',1,1,'Active',SYSUTCDATETIME(),SYSUTCDATETIME(),N'Phố cổ Hội An'),
 (@t1,N'uat/t1-c',N'https://picsum.photos/seed/hoian-c/1200/675',NULL,3,0,'Active',SYSUTCDATETIME(),SYSUTCDATETIME(),N'Bà Nà Hills');
INSERT commerce.TourMedia(tour_id,cloudinary_public_id,delivery_url,caption,sort_order,is_primary,lifecycle_status,created_at,updated_at,deleted_at,alt_text)
 VALUES(@t1,N'uat/t1-deleted',N'https://picsum.photos/seed/deleted/1200/675',NULL,4,0,'Deleted',SYSUTCDATETIME(),SYSUTCDATETIME(),SYSUTCDATETIME(),N'Ảnh đã xóa');

-- Published reviews for T1 (4.67 -> 4.7, 3 reviews)
INSERT planning.Itineraries(traveler_user_id,source_type,title) VALUES(@traveler,'Manual',N'UAT');
DECLARE @it BIGINT = SCOPE_IDENTITY();
INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status) VALUES
 ('UAT-1',@traveler,@it,0,0,'Completed'),('UAT-2',@traveler,@it,0,0,'Completed'),('UAT-3',@traveler,@it,0,0,'Completed');
INSERT social.TripReviews(booking_id,traveler_user_id,tour_id,overall_rating,title,content,publish_display_name,public_display_name,publication_status,created_at,edit_deadline,updated_at)
 SELECT booking_id,@traveler,@t1,CASE booking_code WHEN 'UAT-2' THEN 4 ELSE 5 END,N'Tuyệt vời',N'Chuyến đi rất đáng nhớ.',0,N'Traveler','Published',SYSUTCDATETIME(),DATEADD(DAY,7,SYSUTCDATETIME()),SYSUTCDATETIME()
 FROM commerce.Bookings WHERE booking_code LIKE 'UAT-%';

-- T2: minimal published tour (no media, no schedule, no rating, no terms)
INSERT commerce.Tours(operator_user_id,title,description,destination_summary,base_price,duration_days,status,published_at)
VALUES(@opOk,N'Huế một ngày – bản tối giản',NULL,N'Huế',700000,1,'Published',DATEADD(DAY,-1,SYSUTCDATETIME()));
DECLARE @t2 BIGINT = SCOPE_IDENTITY();
INSERT commerce.TourDestinations(tour_id,destination_id,sequence_no) VALUES(@t2,@hueId,1);

-- T3: published tour whose only departure is sold out
INSERT commerce.Tours(operator_user_id,title,description,destination_summary,base_price,duration_days,status,published_at,cancellation_policy_template_id)
VALUES(@opOk,N'Bà Nà Hills trong ngày – hết chỗ',N'Một ngày trọn vẹn trên Bà Nà.',N'Đà Nẵng',990000,1,'Published',DATEADD(DAY,-2,SYSUTCDATETIME()),@policy);
DECLARE @t3 BIGINT = SCOPE_IDENTITY();
INSERT commerce.TourDestinations(tour_id,destination_id,sequence_no) VALUES(@t3,@dnId,1);
INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,meeting_point,total_capacity,reserved_capacity,status,adult_price)
 VALUES(@t3,DATEADD(DAY,12,SYSUTCDATETIME()),DATEADD(DAY,12,DATEADD(HOUR,9,SYSUTCDATETIME())),N'Cầu Rồng',6,6,'Scheduled',990000);

-- Hidden tours (must be 404 for the public)
INSERT commerce.Tours(operator_user_id,title,description,destination_summary,base_price,duration_days,status) VALUES
 (@opOk,N'HIDDEN draft',N'x',N'Hội An',1000000,1,'Draft'),
 (@opOk,N'HIDDEN pending',N'x',N'Hội An',1000000,1,'Pending'),
 (@opOk,N'HIDDEN rejected',N'x',N'Hội An',1000000,1,'Rejected'),
 (@opOk,N'HIDDEN inactive',N'x',N'Hội An',1000000,1,'Inactive');
INSERT commerce.Tours(operator_user_id,title,description,destination_summary,base_price,duration_days,status,published_at) VALUES
 (@opOk,N'HIDDEN approved not yet published',N'x',N'Hội An',1000000,1,'Approved',DATEADD(DAY,2,SYSUTCDATETIME())),
 (@opPending,N'HIDDEN pending operator',N'x',N'Hội An',1000000,1,'Published',DATEADD(DAY,-1,SYSUTCDATETIME())),
 (@opLocked,N'HIDDEN locked operator',N'x',N'Hội An',1000000,1,'Published',DATEADD(DAY,-1,SYSUTCDATETIME()));

-- Large signed Int64 identity
SET IDENTITY_INSERT commerce.Tours ON;
INSERT commerce.Tours(tour_id,operator_user_id,title,description,destination_summary,base_price,duration_days,status,published_at)
VALUES(9007199254740993,@opOk,N'Tour id lớn (Int64)',N'Kiểm tra id lớn đi qua BFF và trình duyệt.',N'Hội An',800000,1,'Published',DATEADD(DAY,-1,SYSUTCDATETIME()));
SET IDENTITY_INSERT commerce.Tours OFF;

COMMIT TRANSACTION;
SELECT tour_id, status, title FROM commerce.Tours ORDER BY tour_id;
