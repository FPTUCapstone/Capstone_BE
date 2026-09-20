# TM-70 / UC-24 — Implementation & Verification Plan

**Phiên bản:** 1.2 — 19/09/2026. **Trạng thái:** owner đã duyệt D1 nhiều vùng và triển khai Database/Backend ngày 19/09/2026; D0, D2–D8 giữ nguyên. Web/Mobile UI và tích hợp client được hoãn sang Phase 2.

**Owner:** Mai Nguyễn Tiến Đạt / datmnt. **Spec bắt buộc:** [TM-70-spec.md](../specs/TM-70-spec.md).

Phạm vi được duyệt: ba bộ lọc điểm đến, ngày đi, mức giá. Ngày 19/09/2026, owner chốt và duyệt D1 nhiều vùng: một tour được tìm theo mọi vùng Tour Operator chọn là nơi tour đi qua. Không tự mở rộng thành full UC-24 SRS, recommendations, Tour Details, booking, map hay media.

**Ranh giới với khám phá POI:** Gắn vùng vào tour chỉ phục vụ tìm tour. POI `Active` gần vị trí khách là dữ liệu danh mục độc lập, không phụ thuộc điểm dừng chính thức (`TourItineraryItems`) hay `TourDestinations`; nếu hiển thị phải phân biệt “gợi ý gần bạn, ngoài lịch trình”. Phase 1 không triển khai gợi ý theo toàn tuyến hoặc thông báo tự động sắp đến POI. Không dùng việc thiếu gắn POI của operator làm điều kiện ẩn POI khỏi chức năng khám phá.

**Thứ tự phát hành đã duyệt:** Phase 1 = G2 + G3 và toàn bộ mục Backend/Database; Phase 2 = G1 + G4 + G5 cho Web/Mobile và tích hợp client. Phase 1 không sửa source FE/Mobile và không tuyên bố AC dành riêng cho client đã pass.

## 1. Kết quả cần bàn giao và cổng kiểm tra

| Gate | Đầu ra bắt buộc | Không được vượt gate nếu |
| --- | --- | --- |
| G0 — Quyết định | Bản đầu đạt 15/09; D1 nhiều vùng và DB/API contract được owner duyệt 19/09 | Business semantics hoặc contract thay đổi sau duyệt mà chưa cập nhật spec/plan |
| G1 — Thiết kế | Web screen spec, Mobile screen spec, frame audit và responsive states được duyệt | Chưa đọc đúng UC-24 frame hoặc mockup còn field/API giả |
| G2 — Baseline | Nhánh/base/author/cwd đúng, deps khớp lockfile, full build/test baseline, config checklist | Build chỉ chạy với env máy cá nhân; SQL tests không chạy được; dirty changes chưa phân loại |
| G3 — DB & Backend | Migration + full schema tương đương, query/endpoint/OpenAPI, SQL integration pass | Data migration làm mất/sửa dữ liệu không duyệt; SQL skip; field/predicate chưa đúng |
| G4 — Web/Mobile | Feature tests, responsive review, real API integration, không regression auth/public routes | Chỉ UI fixture, mock HTTP hoặc happy path |
| G5 — Tích hợp | SQL → BE → cả client; UC-26 handoff thật; fresh availability; performance evidence | TM-72 chưa có route thật, counter không được xác nhận, test thiết bị còn thiếu |
| G6 — Review/PR | Final-head evidence, diff sạch, description đúng, review findings addressed | Chưa được phép push/PR; CI đỏ/skipped gate; findings chưa xử lý |

Có thể review G3 độc lập trong khi TM-72 đang chờ, nhưng không gắn “Done end-to-end” cho G5. Đổi gate hoặc chấp nhận release thiếu dependency phải được owner phê duyệt rõ.

## 2. Quy tắc làm việc để không lặp lại lỗi cũ

1. Đọc AGENTS.md, CONTRIBUTING.md, README, CODEBASE_RULES, PR template của từng repo **trên đúng base sẽ dùng**, cùng rules/checklist v2.0.
2. Ghi lại HEAD, origin/develop, branch, remote, git status và cwd; phân biệt code trên develop với code chỉ có trong TM-58 review branch.
3. Không checkout/reset/stash tự động, không delete worktree, không thêm 7 file UC-52 vào TM-70. Không dùng git add . ở workspace chứa nhiều task.
4. Không đổi pubspec.lock hiện đang sửa từ trước trong Mobile/main. Mọi lockfile mới phải có lý do và diff package rõ ràng.
5. Đặt nhánh có datmnt, base develop ở **cả BE, FE và Mobile**; không base Mobile/main như lần trước.
6. Không đổi author của commit người khác. Khi được yêu cầu commit, kiểm tra git user.name/email và GitHub account của người dùng; không tự đoán email. Co-author theo quy định repo nếu áp dụng.
7. Mỗi atomic task: viết test đỏ có lý do đúng → implement tối thiểu → test xanh → review scope → ghi evidence. Không xem test không chạy/không tìm thấy test là RED hợp lệ.
8. Không gộp formatting toàn repo. Kiểm tra cả diff staged/unstaged và base...HEAD; review final diff chứ không chỉ commit cuối.
9. Mock/fixture chỉ dùng test và thiết kế có nhãn. Feature production không fallback mock khi API hỏng.
10. Full build và CI đều phải thực hiện. Không lấy “npm run dev chạy”, “analyze pass”, hoặc HTTP 200 làm bằng chứng nghiệm thu.
11. Không claim SQL pass khi có skip; không lấy số lượng test từ task trước. Mỗi bằng chứng gắn exact SHA và môi trường.
12. Chưa commit, push, tạo PR, cập nhật Jira, resolve conversation hoặc merge nếu chưa được yêu cầu.

## 3. Branch và workspace dự kiến

Các tên dưới đây là **đề xuất sau G0**, chưa được tạo. Kiểm tra nhánh/đường dẫn đã tồn tại trước mọi thao tác; nếu có worktree đúng task thì dùng lại, không ghi đè.

| Repo gốc | Nhánh đề xuất | Worktree đề xuất để tránh trộn task đang review | PR base |
| --- | --- | --- | --- |
| D:/CapStone/Capstone_BE | feature/datmnt-search-tours | D:/CapStone/Capstone_BE_tm70 | develop |
| D:/CapStone/Capstone_FE | feature/datmnt-search-tours | D:/CapStone/Capstone_FE_tm70 | develop |
| D:/CapStone/Capstone_Mobile | feature/datmnt-search-tours | D:/CapStone/Capstone_Mobile_tm70 | develop |
| D:/CapStone/Capstone_Docs | docs/datmnt-search-tours | D:/CapStone/Capstone_Docs_tm70 | Base theo quy tắc Docs/remote thực tế, kiểm tra trước |

Worktree là cùng repo trên nhánh khác, không phải bản demo ngoài repo. Mỗi runbook phải ghi đúng worktree chứa feature, URL/port đang phục vụ và SHA. Không đưa đường dẫn .codex-tmp cũ vào lệnh test TM-70.

Nếu owner muốn thực hiện trực tiếp trong repo gốc, chỉ chuyển nhánh khi working tree sạch và các task khác đã được bảo toàn. Không tự chuyển bản sửa giữa các nhánh.

Lệnh kiểm tra read-only trước khi tạo nhánh, chạy riêng cho từng repo:

~~~powershell
git status --short
git branch --show-current
git rev-parse HEAD
git rev-parse origin/develop
git worktree list
git remote -v
git config user.name
git config user.email
~~~

Fetch develop cần được chạy trước lúc triển khai. Nếu head/base đã đổi so với spec, re-audit collision của routes, entities, migrations, DTO và dependencies; không áp nguyên plan cũ vào code mới.

## 4. Tài liệu canonical cần cập nhật sau duyệt

Không sửa tài liệu TM-58 đang dở trên nhánh Docs hiện tại để “tiện thể” thêm TM-70.

| Tài liệu trong Docs worktree | Nội dung |
| --- | --- |
| requirements/mvp/search-tours-mvp.md | Thu hẹp đúng D0; tách mục deferred; BR/CR giữ nguyên |
| ux/user-flows/search-tours-user-flow.md | Guest/Traveler, submit, error, retry, page, detail-back, stale |
| ux/screen-specifications/search-tours-screen-spec.md | Mobile phase 1, gỡ keyword/traveler count/rating/sort/discount không hỗ trợ |
| ux/web-screen-specifications/search-tours-web-screen-spec.md — mới | Public Web route, layout 320–1440, states, accessibility |
| ux/stitch-prompts/search-tours-stitch-prompt.md | Chỉ cập nhật nếu tiếp tục dùng; không dùng prompt rộng hơn làm source of truth |
| api/contracts/tour-search-api.md — mới | Request/response/errors/nullability/ID/date/price/cache, examples, AC references |
| Tài liệu verification TM-70 — mới, theo convention Docs | Evidence matrix, migration instructions, dependency status, final heads |

Trong mỗi source repo tạo specs/TM-70-spec.md và plans/TM-70-plan.md theo AGENTS, dẫn tới bản contract chung và đóng dấu phiên bản đã duyệt. Khi sao chép nội dung sang repo, sửa relative links cho đúng vị trí; không giữ link trỏ nhầm giữa worktree.

Không sửa trực tiếp SRS gốc để che mâu thuẫn MSG29/giá/ảnh. Tạo reconciliation note có owner duyệt; sửa SRS chỉ trong một tác vụ tài liệu được cho phép.

## 5. Backend — atomic implementation

Các đường dẫn dưới đây tương đối với Backend worktree; file “mới” là mục tiêu đề xuất, không phải đã tồn tại. Re-audit tên lớp trên develop trước khi tạo để tránh entity trùng với task operator đang triển khai.

### B0 — Baseline và contract harness

**Phụ thuộc:** G0 đã đạt; G2. G1 được hoãn và không chặn Database/Backend Phase 1.

- Chạy restore, full Release build và test hiện có; xác minh SDK theo global.json, không theo dòng .NET 8 trong SRS cũ. Baseline đang target net10.0, global.json 10.0.100 với latestFeature.
- Kiểm tra pipeline Result<T>, HandleFailure, binding validation, enum converter, TimeProvider, cancellation và DI hiện tại.
- Tạo fixture JSON từ spec cho 200/empty/400/500/null/ID lớn để dùng chung consumer tests. Gắn nhãn dữ liệu test.
- Viết endpoint/contract RED cho GET /api/v1/tours chưa tồn tại, nhưng không coi request crash host do thiếu config là RED đúng.

**Test/evidence:** AC-01, AC-18, AC-29; command + output discovery list.

### B1 — SQL preflight và migration

**Files:**

- database/tripmate_schema_v7.sql.
- database/migrations/YYYYMMDD_add_tour_search_fields.sql — ngày thực tế lúc triển khai, tránh trùng tên.
- database/README.md.
- tests/TripMate.Api.IntegrationTests/Tours/TourSearchMigrationTests.cs — mới.
- tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj — copy migration/test fixture vào output đúng path.
- Test support cho old-schema baseline, không sửa schema hiện tại trong test rồi gọi đó là migration test.

**RED:**

1. Fresh schema thiếu `catalog.Destinations`, `commerce.TourDestinations` hoặc whole-dong constraint; không còn dùng một cột `Tours.destination` làm nguồn tìm kiếm.
2. DB theo schema cũ có dữ liệu hợp lệ; migrate hai lần phải giữ rows, identities và FK.
3. DB có giá lẻ: migration từ chối, không làm tròn và không để half-applied changes.
4. Bảng/PK/FK/unique/index/constraint cùng tên nhưng sai shape: báo mismatch, không silently skip.

**GREEN:**

- Transactional, additive, idempotent SQL; guard object đúng schema; checks có tên; không chạy drop/recreate database.
- Hai bảng vùng + liên kết được tạo idempotent; tour cũ chưa gắn vùng giữ nguyên, không backfill giả; whole-dong constraint được check/trusted, không dùng NOCHECK để lách dữ liệu.
- `catalog.Destinations`: BIGINT identity + tên NVARCHAR(300) Vietnamese_100_CI_AS duy nhất. `commerce.TourDestinations`: FK Tour/Destination, PK (tour_id,destination_id), `sequence_no > 0` duy nhất theo tour, index trên `destination_id` (clustered PK mang `tour_id`). Không seed vùng giả vào DB thật.
- Rà index hiện có; thêm chỉ sau query plan B3 cho thấy cần; cập nhật cùng full schema và migration.
- Old baseline lấy từ revision đã ghi trong test fixture hoặc snapshot kiểm soát phiên bản; test không phụ thuộc network hoặc git history CI shallow.

**Exit:** AC-28; fresh/upgraded metadata tương đương; rerun sạch; migration fail-safe.

### B2 — Domain và EF mapping

**Files mới dự kiến:**

- src/TripMate.Domain/Entities/Tour.cs, TourSchedule.cs.
- src/TripMate.Domain/Enums/TourStatus.cs, TourScheduleStatus.cs.
- src/TripMate.Infrastructure/Persistence/Configurations/TourConfiguration.cs, TourScheduleConfiguration.cs.
- tests/TripMate.Infrastructure.UnitTests/Persistence/TourPersistenceModelTests.cs.

**Files hiện hữu cần cập nhật đúng scope:**

- src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs.
- src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs.
- tests/TripMate.Application.UnitTests/TestUtilities/TestDbContext.cs và các test doubles implement interface khác được rg tìm ra.

**RED/GREEN:**

- Map BIGINT long, identity, operator FK, precision (12,2), lengths/status check, publication nullable, UTC converter của repo; thêm read mapping Destination/TourDestination, FK/composite key và route order.
- Mapping date UTC qua AsUtcDateTime2 hiện hữu; round-trip SQL đọc ra kind/offset đúng.
- Seed entities qua fixture/navigation hoặc SQL parameterized; không gán FK = 0 rồi hy vọng EF sửa.
- Không thêm mutation domain methods/public create endpoint ngoài nhu cầu đọc task này.

**Exit:** AC-02, AC-07–11, AC-18, AC-28; cả solution/test projects compile.

### B3 — Query, validator và consistency

**Files mới dự kiến:**

- src/TripMate.Application/Features/Tours/Search/SearchToursQuery.cs.
- SearchToursQueryValidator.cs, SearchToursQueryHandler.cs.
- PagedToursResponseDto.cs, TourSearchItemDto.cs.
- Helper pure feature-scoped cho normalized criteria/date interval nếu cần, không generic repository.
- tests/TripMate.Application.UnitTests/Features/Tours/Search/...
- tests/TripMate.Api.IntegrationTests/Tours/SearchToursSqlServerTests.cs.

**Thứ tự RED → GREEN:**

1. Destination literal/length/NFC/casing; numeric price/date parsing/validation. Tạo SQL fixtures Tour Đà Nẵng → Hội An gắn cả hai vùng; mỗi query vùng phải match đúng một tour trong items/count.
2. Public predicate đủ Tour + Operator + User, dùng một clock testable.
3. Future schedule + date interval UTC; capacity representative có tie-break.
4. `EXISTS`/`Any` qua TourDestinations để lọc mà không nhân tour; one tour/row, default ordering, count/paging, offset guard. Project `destinations: string[]` theo `sequence_no` chỉ sau khi paginate tours; bảo đảm không N+1.
5. Read-only Serializable transaction theo abstraction có sẵn; không query N+1; projection và cancel.
6. Runtime data inconsistency riêng tour → unknown theo spec; lỗi cả query không trở thành partial success.
7. Concurrency test điều phối hai SQL connections bằng barrier/TCS, không flaky sleep; chứng minh không count/items lệch do publication và capacity race.

**Đo kỹ thuật:** lưu SQL query shape, execution plan/số query khi 20 và 100 items; không dùng EF InMemory để kết luận collation, LIKE escape, transaction, constraints hoặc datetime2 đúng.

**Exit:** AC-02–19. Nếu Serializable gây blocking vượt NFR, dừng tối ưu bằng ADR có test consistency thay thế; không tự bỏ transaction hoặc dùng NOLOCK.

### B4 — Endpoint, errors, OpenAPI, public access

**Files mới:** src/TripMate.Api/Controllers/ToursController.cs; tests/.../Tours/SearchToursEndpointTests.cs, SearchToursJwtBearerTests.cs, ToursOpenApiTests.cs.

- GET /api/v1/tours AllowAnonymous, thin controller. Ok(dto) không gọi Success envelope helper cũ.
- Parse/validate query strict đúng field, duplicate keys/unknown keys; raw-binding errors và FluentValidation cùng camelCase.
- OpenAPI thể hiện ID strings, `destinations` array, nullable fields còn lại, 200/400/500 thực tế, no fabricated business error bodies.
- Header no-store cho success và failure; cache không kế thừa policy khác.
- Đúng guest/token optional; không đổi auth scheme/middleware global nếu không cần. Nếu global interceptor/handler cản public route, sửa minimal có regression tests protected routes.
- Ngăn error message infrastructure leak; không log auth headers/free-text query thô.

**Exit:** AC-01, AC-15, AC-18–19, AC-22, AC-25, AC-32.

### B5 — CI và release readiness BE

- SQL Server test service đã có trong .github/workflows/backend-tests.yml; tái dùng TRIPMATE_SQLSERVER_TEST_CONNECTION.
- Thêm migration test data đúng csproj copy rule; thiếu artifact phải fail rõ, không skip.
- CI guard kiểm tra biến SQL test tồn tại và số test SQL TM-70 executed > 0, skipped = 0. Các SQL suite liên quan phải thực sự chạy; không dùng aggregate xanh che một nhóm bị skip.
- Build Release, format verify, all tests, dependency scan, diff checks; ghi head và log.
- Không deploy DB production bằng test account hoặc mở privilege rộng để tiện test.

## 6. Web — Phase 2, chưa thực hiện trong lượt Backend/Database

### W0 — Design/contract/baseline

**Phụ thuộc:** G0, G1, G2 và contract B0; real integration cần G3.

- Đọc đúng frame UC-24 bằng connector/export. Ghi file key/node/version, ảnh desktop/mobile/tablet nếu có, mapping field ↔ DTO và responsive deviations.
- Không tìm được frame: cho owner duyệt wireframe/screen spec UC-24 thay thế; không khẳng định Figma-perfect.
- Full build develop với env được cấp đúng; kiểm tra lỗi Firebase import tại /admin/login, /login và navigation công khai.
- .github/workflows/ci.yml baseline chưa có npm test: thêm test step khi implement; không giữ CI chỉ lint/typecheck/build.

### W1 — Contract parser và API bridge

**Feature root:** src/features/public/tour-search/.

**Files đề xuất:** types.ts, validation.ts, api/tour-search-client.ts, api/tour-search-server.ts, api/parse-tour-search-response.ts; src/app/api/tours/route.ts.

- RED cho 200/null/ID lớn/malformed/empty, 400 field errors, 500/no body, HTML 200, disconnect, timeout và duplicate query.
- Runtime parser giữ tourId/scheduleId string, kiểm tra integer VND/capacity/count, không force-cast unknown JSON.
- BFF origin từ biến server-only **TOUR_SEARCH_API_BASE_URL** đề xuất, giá trị base host không có /api/v1; nối duy nhất fixed path. Không nhận URL từ browser, không forward cookies/bearer không cần.
- Nếu project có convention tên env/server proxy tương đương sau re-audit thì reuse và cập nhật contract/runbook; không tạo hai nguồn cấu hình mâu thuẫn.
- Timeout upstream đề xuất 10s; giữ HTTP 400, lỗi upstream có semantics 502/503/504 như spec; no-store và cancellation.
- Không dùng dummy Firebase values hoặc fallback mock để làm xanh build.

**Exit:** AC-05–06, AC-10–11, AC-14–15, AC-18, AC-22, AC-25.

### W2 — State, URL, submit/race

**Files đề xuất:** hooks/use-tour-search.ts, model/search-state.ts, tests cạnh feature theo convention Vitest.

- RED mọi transition ở spec §6: edit không fetch, submit/clear/page/retry, giữ previous list, draft khác applied, A chậm B nhanh, back/forward, unmount.
- Dùng AbortController và requestVersion; URL parse/serialize thuần có test; không request loop khi canonicalize URL.
- Khi back/pageshow BFCache hoặc visibility resume, revalidate và không expose số chỗ stale như fresh.
- Mỗi field lỗi được map bằng key/status; không parse message English.

**Exit:** AC-20–25.

### W3 — UI và navigation

**Files đề xuất:** src/app/tours/page.tsx; components/TourSearchScreen.tsx, TourSearchForm.tsx, TourSearchCard.tsx, TourSearchResults.tsx, TourSearchPagination.tsx, TourSearchStatus.tsx; resources/en.ts.

**Điểm tích hợp hiện hữu:** src/components/navigation/PublicNavigation.tsx và src/lib/routes.ts sau khi xác minh tên/path thực tế.

- Server page chỉ shell/metadata; client island cho interactions. Không query SQL từ Next.
- Nếu client dùng useSearchParams, đặt boundary đúng; không chỉ chạy dev để kiểm tra Suspense/prerender. Xem [Next.js useSearchParams](https://nextjs.org/docs/app/api-reference/functions/use-search-params).
- Tách page /tours với route handler /api/tours, không đặt page.tsx và route.ts cùng segment.
- Availability no-store xuyên BFF và browser, không dựa vào mặc định caching; xác minh thêm ở production runtime. Tham khảo [Next.js fetch](https://nextjs.org/docs/app/api-reference/functions/fetch).
- Responsive theo spec; text English resource, không chuyển public navbar thành admin, không sửa màu/font toàn hệ thống để giống frame.
- Nút Tour Details gắn adapter đã thống nhất TM-72; không href #/route 404 được xem là chức năng hoàn tất.

**Exit:** AC-24, AC-26–27; screenshot cùng viewport Figma và viewport mở rộng, báo mọi deviation.

### W4 — Verification Web

- Vitest + testing-library cho logic/component; thêm browser E2E runner chỉ khi repo chưa có và được duyệt dependency, không giả DOM test thay E2E.
- npm ci, npm run lint, npm run typecheck, npm test, npm run build; chạy npm run start kiểm tra /tours và các auth/admin routes bị ảnh hưởng.
- Test empty config tách khỏi configured build: thiếu upstream → error đúng; thiếu Firebase → xử lý theo chính sách hiện hữu hoặc fix được duyệt, không conceal.
- Browser Network phải chứng minh /api/tours nhận đúng DTO từ BE, không endpoint POI hoặc file JSON fixture.
- CI phải chạy tests + build với môi trường tái lập; ghi test counts thực tế ở head cuối.

## 7. Mobile — Phase 2, chưa thực hiện trong lượt Backend/Database

### M0 — Design/route/environment

**Phụ thuộc:** G0/G1/G2, contract B0; branch từ develop.

- Kiểm tra platform được hỗ trợ và thiết bị thật bằng flutter devices/flutter emulators/flutter doctor; không giả Android emulator đã tồn tại.
- Map public /tours ngoài guard /traveler và /operator; Guest vào không login, Traveler entry không trùng tab/stack.
- Đối chiếu token/theme hiện hữu, không copy Technical Mapping/State Matrix vào UI.

### M1 — Model/data source/repository/use case

**Feature root:** lib/features/tours/ hoặc tên tương đương được AGENTS yêu cầu.

**Files đề xuất:**

- domain/entities/tour_search_item.dart, tour_search_page.dart, tour_search_criteria.dart.
- domain/repositories/tour_search_repository.dart; domain/usecases/search_tours.dart.
- data/models/tour_search_item_model.dart, tour_search_page_model.dart.
- data/datasources/tour_search_remote_data_source.dart.
- data/repositories/tour_search_repository_impl.dart.

**Điểm sửa hiện hữu:** lib/core/di/service_locator.dart; lib/core/error/failures.dart chỉ nếu cần optional fieldErrors.

- RED parse IDs lớn dưới Dart native và web, nulls, enum, 400/500/timeout, JSON sai kiểu.
- Future<T> + Failure của repo; sealed/final classes phải chỉnh hợp lệ cùng thư viện, không thêm Either dependency.
- DioClient chung, GET /api/v1/tours, không hardcode IP. Public request không cần refresh credential; nếu thêm skipAuth request option, auth interceptor phải có regression tests.
- Không cache result vào offline DB; giữ criteria trong state, không lưu availability qua phiên.

**Exit:** AC-01, AC-04–11, AC-14–15, AC-18, AC-22, AC-25.

### M2 — Cubit và xử lý trạng thái

**Files:** presentation/cubit/tour_search_cubit.dart, tour_search_state.dart; tests Cubit/repository bằng fakes có barrier điều khiển thời điểm trả response.

- States biểu diễn initial/loading/success/empty/failure và previous results/freshness độc lập, không chỉ bool loading.
- Draft/requested/displayed criteria tách biệt theo spec; validate trước request và giữ inline server errors.
- CancelToken + requestVersion; kiểm tra Cubit closed; không emit sau dispose.
- Pagination 20, Previous/Next, page-preservation; không tự đổi thành infinite scroll từ quyết định cũ của TM-58.
- Resume/detail pop revalidate; clear/retry giữ semantics giống Web.

**Exit:** AC-20–25.

### M3 — UI/navigation/responsive

**Files:** presentation/pages/tour_search_page.dart; presentation/widgets/*; feature text resources; lib/app/router/app_routes.dart, app_router.dart; Guest/Traveler entrypoints được G1 duyệt.

- SafeArea, list scroll, filter sheet với keyboard, tối thiểu 48dp, English text, timezone/VND fixed.
- Không hardcode card height; 320px + text scale 2.0 vẫn đủ nội dung/action; date picker không serialize theo timezone thiết bị.
- Widget tests cho error/empty/no schedule/soldout/unknown/`destinations: []`; semantics labels cho price/date/state.
- Handoff tourId string sang TM-72; preserve route stack + filters/page/scroll.

**Exit:** AC-24, AC-26–27; screenshots Guest/Traveler, portrait/landscape/keyboard.

### M4 — Verification Mobile

- flutter pub get; dart format verify; flutter analyze; flutter test; flutter build apk --debug với API env phù hợp.
- Android emulator hoặc máy thật chạy against BE/SQL test data; browser chỉ là kiểm tra bổ sung, không thay Android.
- Flutter Chrome support phải được cấu hình thật; không chạy flutter create . chỉ để dập warning ngoài scope.
- iOS build/test cần macOS + device/simulator. Trên Windows ghi NOT RUN và owner kiểm thử iOS trước khi tuyên bố nghiệm thu iOS; không ghi “all platforms pass”.
- Không dùng release mode để che assertion ViewInsets; ghi Flutter/browser versions và reproduce ở thiết bị hỗ trợ nếu gặp lại.

## 8. Bộ dữ liệu và kịch bản test SQL thực

Tạo DB test riêng theo harness TripMate_Test_<GUID>; cleanup chỉ DB do test vừa tạo, kiểm tra prefix/ID và lifecycle. Tuyệt đối không trỏ migration/concurrency fixture vào TripMateDb của người dùng.

Fixture data deterministic với TimeProvider cố định; parent User/Operator phải hợp lệ trước Tour/Schedule. Test của mỗi case cách ly dữ liệu để totalCount kỳ vọng không phụ thuộc thứ tự test.

| Nhóm | Dữ liệu tối thiểu | Kỳ vọng chính |
| --- | --- | --- |
| F1 visibility | Từng status Tour, null/future publication; owner Active/khác; operator Approved/khác | Không leak qua count/items |
| F2 destination | Tour Đà Nẵng → Hội An gắn hai vùng, tour chỉ Hội An, tour không vùng; lower/upper case, dấu tổ hợp, literal %, _, brackets/quote | Tìm từng vùng trả đúng mọi tour được gắn, mỗi tour một lần trong items/count; `destinations` theo thứ tự; query parameterized |
| F3 ngày | Lịch đúng lower bound, ngay trước lower, ngay trước upper, đúng upper, quá khứ, Scheduled/Cancelled/Completed | Nửa mở theo giờ VN |
| F4 lịch/chỗ | Nhiều lịch, full sớm hơn available, capacity 1/reserved 0, capacity=reserved, no schedule | Một tour/row, representative/remaining đúng |
| F5 giá | 0, min/max đúng biên, 9,999,999,999; giá .50 chỉ fixture migration invalid | Không làm tròn, không nhầm missing với 0 |
| F6 phân trang | Ít nhất 45 tour public, title trùng, tour nhiều schedule | 20/20/5, total 45, tie-break ID |
| F7 numeric/null | BIGINT > 2^53 qua fixture chuyên dụng; nullable fields | Không mất precision; null không thành 0 |
| F8 contention | Hai connection với synchronization barrier, unpublish/capacity update | Consistency trong request, freshness request sau |
| F9 failure | Timeout/offline ở boundary có kiểm soát, malformed payload ở BFF fakes | HTTP/error state đúng; không success giả |
| F10 schema | Fresh/new, snapshot old + migrate + rerun, price fraction, shape mismatch | Migration fail-safe và parity |

Fakes/in-memory thích hợp cho validator/client state; F1–F8/F10 cần SQL Server cho những assertion về SQL. Không dùng mocked API để thay AC-30.

Tạo integration seed **tùy chọn chỉ dev/test** có tên/nhãn rõ. Muốn dùng dữ liệu nghiệp vụ hiện tại: kiểm tra read-only trước, xác nhận DB/server và xin phép owner nếu cần thêm/sửa row. Không chèn ảnh tour giả để làm UI đẹp.

## 9. Runbook kiểm thử dự kiến sau triển khai

Các lệnh dưới đây là kế hoạch sử dụng sau khi các worktree ở §3 được tạo và config được cấp. **Không có lệnh nào trong mục này đã được chạy như bằng chứng TM-70 hiện tại.**

### 9.1 Backend quality gates

~~~powershell
Set-Location D:\CapStone\Capstone_BE_tm70
dotnet --info
dotnet restore TripMate.slnx
dotnet build TripMate.slnx -c Release --no-restore
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet test TripMate.slnx -c Release --no-build --logger trx --results-directory TestResults
dotnet list TripMate.slnx package --vulnerable --include-transitive
git diff --check
git diff --cached --check
git diff --check origin/develop...HEAD
~~~

Trước dotnet test, người chạy cấu hình TRIPMATE_SQLSERVER_TEST_CONNECTION theo hướng dẫn test harness; chỉ dùng SQL Server test đã được phép. Không paste connection string vào PR/chat/log. Guard runner phải fail nếu biến thiếu hoặc test SQL bắt buộc bị skip.

Mỗi lệnh là gate riêng; PowerShell không mặc định dừng script chỉ vì native command exit khác 0. Script tự động phải kiểm tra LASTEXITCODE sau mỗi lệnh và exit ngay khi fail, tránh lệnh cuối xanh che build lỗi.

Nếu format baseline đã lỗi: lưu baseline evidence, dùng scoped check đúng files TM-70 và báo blocker baseline; không format cả solution hàng chục file ngoài scope hoặc tự gọi toàn bộ format “pass”.

### 9.2 Web quality gates

~~~powershell
Set-Location D:\CapStone\Capstone_FE_tm70
npm ci
npm run lint
npm run typecheck
npm test
npm run build
git diff --check
git diff --cached --check
git diff --check origin/develop...HEAD
npm run start
~~~

npm run start chỉ sau các gate trước pass; kiểm tra production http://localhost:3001/tours, không chỉ next dev. Nếu port bận, xác định process/cwd; không kill process của task khác. Dùng port khác có ghi rõ và đồng bộ origin/env.

### 9.3 Mobile quality gates

~~~powershell
Set-Location D:\CapStone\Capstone_Mobile_tm70
flutter --version
flutter doctor
flutter devices
flutter pub get
dart format --output=none --set-exit-if-changed lib test
flutter analyze
flutter test
flutter build apk --debug --dart-define=APP_ENV=development --dart-define=API_BASE_URL=http://10.0.2.2:5021
git diff --check
git diff --cached --check
git diff --check origin/develop...HEAD
~~~

Để chạy, dùng device ID thật từ flutter devices. Không copy chuỗi <ID-Android-Emulator> vào PowerShell. Không đặt các flag POI_CATEGORY_PREVIEW của TM-58 vào lệnh TM-70.

### 9.4 Chốt network/config trước smoke test

| Topology | Base URL cần dùng | Lưu ý |
| --- | --- | --- |
| BE chạy launch profile http hiện tại | http://localhost:5021 | Đây là port đã đọc ở develop, khác mặc định Android config 5000 |
| Next BFF trên host → BE native | TOUR_SEARCH_API_BASE_URL=http://localhost:5021 | Không NEXT_PUBLIC cho biến server-only này |
| Android emulator chuẩn → BE host | API_BASE_URL=http://10.0.2.2:5021 | 10.0.2.2 không phải hostname để browser host dùng |
| Flutter Chrome/Edge trên host | API_BASE_URL=http://localhost:5021 | Cần web support và CORS allowlist đúng origin/port |
| Điện thoại Android thật → BE host | IP LAN thật của host + port được publish | Cùng mạng, binding/firewall được cấu hình có giới hạn; không mặc định 192.168.56.1 |
| BE Docker, FE chạy host | Host published port đã xác minh | docker compose ps/port, không đoán luôn 5000 |
| FE và BE cùng Compose network | Service DNS của BE + container port | localhost trong FE container không trỏ BE container |

Env auth/Firebase hiện hữu: 6 key NEXT_PUBLIC_FIREBASE_* đang required trong config; dùng Web App config được owner cung cấp cho môi trường phù hợp. Không commit .env.local hoặc token; Firebase Web config công khai không phải lý do để đưa server credentials vào NEXT_PUBLIC.

Backend config DB/auth có sẵn theo README; migration phải apply trước app mới. CORS development chỉ cho configured origins; không wildcard để làm test pass. Android HTTP chỉ trong development theo cấu hình platform; production HTTPS.

Với Docker: kiểm tra Docker daemon bằng docker version/compose version, rồi đọc compose services/ports/health. Không chạy compose down -v để “reset DB”; volume có thể chứa dữ liệu người dùng. Dùng service và network thật sau inspection, không hướng dẫn tên service chưa xác minh.

### 9.5 Smoke journey có dữ liệu thật

1. Ghi BE/Web/Mobile SHA, base, cwd, runtime, DB test/server alias an toàn; check config không in secret.
2. Đọc ID và row expected từ SQL test fixture; gọi GET trực tiếp BE, kiểm tra JSON fields/count/date/price/remaining.
3. Mở Guest Web /tours: gõ không có request; submit destination/date/price; đối chiếu ID/card/count với bước 2.
4. Đi trang 2 → mở đúng Tour Details thật → Back, giữ filters/page/scroll và revalidate.
5. Làm tương tự với Android Guest và Traveler; logged-in state chỉ thay navigation không đổi public predicate.
6. Trong DB test, update reserved_capacity có kiểm soát, refresh/resume; cả client phải hiện số mới, không cần rebuild.
7. Trong DB test, chuyển publication sang không công khai; lần request tiếp không còn tour trong count/items. Không sửa Tour thật của operator để demo.
8. Chặn mạng/dừng **BE test instance do người chạy sở hữu**, Retry, sai input, BFF upstream HTML giả trong test harness; chứng minh không báo empty/success.
9. Chụp responsive/keyboard/font scale; không expose secrets trong DevTools/ảnh.

## 10. Traceability: acceptance → implementation → evidence

| Acceptance IDs trong spec | Work packages | Evidence bắt buộc |
| --- | --- | --- |
| AC-01 | B0/B4, W3, M0/M1/M3 | HTTP auth matrix + guest/traveler route tests |
| AC-02 | B1–B4 | SQL visibility items/count, owner eligibility |
| AC-03–06 | B3/B4, W1/W2, M1/M2 | Validator + SQL literal/collation + submit tests |
| AC-07–09 | B2/B3, W1/W3, M1/M3 | UTC-boundary SQL + representative schedule tests |
| AC-10–12 | B1/B3, W1/W2, M1/M2 | Integer/zero/range/AND tests |
| AC-13–16 | B3/B4, W2, M2 | SQL paging + race, URL/page handling |
| AC-17–19 | B3/B4, W1, M1 | Unknown-vs-failure, ID/null contract, no DB write |
| AC-20–23 | W2/W4, M2/M4 | Request-count/race/Retry/Clear/state tests |
| AC-24 | W3/W4, M3/M4, TM-72 handoff | Browser + Android end-to-end video/log |
| AC-25 | B4, W1/W2/W4, M1/M2/M4 | Headers + resume/back and updated counter |
| AC-26–27 | G1, W3/W4, M3/M4 | Screenshot matrix + accessibility + formatting |
| AC-28 | B1/B2/B5 | Fresh/migrate/rerun/invalid-data SQL logs |
| AC-29 | G2, B5, W0/W4, M4 | Clean install/build local + CI exact head |
| AC-30 | G5, §9.5 | SQL row ↔ API ↔ Web/Mobile evidence |
| AC-31 | B3/B5, G5 | Load profile + durations/query count/locks/timeout |
| AC-32 | B4, G6 | Redacted logs/DTO audit + PR evidence reconciliation |

## 11. Performance, regressions và kiểm tra độc lập

### 11.1 Benchmark đề xuất

- Dataset benchmark riêng: 10,000 public/non-public tours hỗn hợp, 100,000 schedules và nhiều vùng/tour; test query không filter và literal contains phổ biến; đo thêm join/filter trên TourDestinations.
- Ramp 1 → 20 → 200 concurrent users, workload đọc, warm-up 2 phút và đo 5 phút mỗi profile; ghi request count, avg/p50/p95/p99/max/error rate và SQL wait/locks.
- So với NFR SRS: avg <= 300ms, max <= 2,000ms, 200 CCU; cold-start báo riêng. Không trộn timeout/error vào denominator rồi bỏ khỏi báo cáo.
- Đo Web LCP target <= 2.5s bằng profile 4G được ghi rõ, production build; tải lần đầu và lần điều hướng tách riêng.
- Đây là workload đề xuất để duyệt G0, không cam kết máy local bất kỳ sẽ chứng minh năng lực production. Nếu thiết bị không đủ tải, báo NOT VERIFIED và chuyển môi trường phù hợp; không giảm CCU rồi ghi đạt 200.

### 11.2 Regression phải chạy

- Backend auth, POI create, TravelGroup idempotency/SQL FK, CORS, exception pipeline và test double compile.
- Web /login, /admin/login, public navbar, existing auth routes, routes không bị biến thành client-only hoặc lỗi prerender vì Firebase.
- Mobile auth/guard, traveler/operator shell, DI registration, constructor Failure cũ, các tests/lockfile đã có.
- Schema migration không tác động idempotency key TravelGroupCreationRequests và schema khác.

### 11.3 Review trước yêu cầu thành viên review

1. Rà spec compliance: AC nào chưa test, rule nào code tự thêm.
2. Rà implementation: SQL predicate/count, timezone, type/precision, secret exposure, cancellation, N+1/locking.
3. Rà UX: thao tác thật trên kích thước nhỏ, state lỗi, không mất draft/applied, đúng navigation.
4. Rà toàn diff: scope, accidental formatting, whitespace, conflict markers, untracked build outputs.
5. Rà evidence: số pass/fail/skip, CI SHA, migration artifacts, exact command, mô tả PR, reviewer conversations.

Review độc lập theo quy tắc repo sau mỗi module hoàn tất; không để người implement tự đánh dấu mọi finding đã resolve mà không test lại. Không tự đăng review/request changes/approve bằng tài khoản người dùng nếu chưa được yêu cầu.

## 12. Evidence template và điều kiện tạo PR

Mỗi run lưu record, không điền sẵn số test kỳ vọng:

~~~text
Task: TM-70 phase 1
Repo / branch / cwd:
Head SHA / origin-develop SHA / timestamp:
Spec + plan version approved by:
Runtime / OS / device / DB test alias:
Command:
Exit code:
Discovered / passed / failed / skipped:
SQL-required cases executed / skipped:
Evidence artifact paths / CI run URL:
AC IDs covered:
Baseline failures:
NOT RUN / BLOCKED + reason / owner / next action:
~~~

Điều kiện request review:

- Scope và D1–D8 đúng spec duyệt; AC evidence đầy đủ cho module.
- Không còn unreviewed source change sau build/test; nếu head đổi phải xác định và chạy lại gate bị ảnh hưởng, final CI đúng head.
- PR base develop (Mobile không main), source có datmnt; commit author đúng user; liên kết Jira TM-70.
- PR description nói rõ phase 1, deferred filters, thiếu ảnh/rating, chưa map, dependency TM-72/counter nếu chưa hoàn tất.
- Ghi counts thật, không hardcode 21/21 hoặc copy 207/286 từ PR cũ; nếu SQL skip ghi chưa đủ điều kiện.
- Đính kèm migration + full schema + README và rollout instructions; không bắt reviewer tìm file SQL ngoài repo.
- git diff --check cho staged/unstaged/base diff; không BOM/EOF/trailing blanks ngoài convention.
- Không đóng conversation chỉ vì đã push; reviewer xác nhận fix trên head cuối. CI xanh và MERGEABLE không thay review/branch protection.

Đề xuất PR theo module: Backend trước; Web và Mobile sau contract backend ổn định. Có thể draft PR phụ thuộc được owner cho phép, nhưng không mark Ready/Done nếu chưa G5. Tạo PR/push/merge là hành động riêng, phải chờ yêu cầu.

## 13. Rollout, rollback và điểm dừng

### Rollout sau khi được phép

1. Owner xác nhận danh mục vùng, mapping tour cũ → mọi vùng thực sự đi qua, timezone và giá; backup/restore plan DB theo quy trình team.
2. Dry run migration trên bản sao được phép; xác minh counts/constraints/schema parity.
3. Apply migration additive vào môi trường mục tiêu được chỉ định, không chạy full schema trên DB có dữ liệu.
4. Deploy BE, kiểm tra guest API + no-store + schema; nếu lỗi dừng trước client.
5. Deploy Web/Mobile đúng API base, kiểm tra routes/details/capacity thật; ghi client/backend version tương thích.
6. Theo dõi error/latency/unknown availability trong cửa sổ rollout do owner chọn; task hiện tại không tự tạo monitor.

### Rollback

- Rollback client hoặc BE về phiên bản đã xác minh; column nullable/index có thể giữ nguyên.
- Không xóa vùng/liên kết/counter/Tour rows hoặc drop database/volume.
- Whole-VND constraint có ảnh hưởng writer: nếu phải nới thì cần migration riêng được duyệt và ghi compatibility; không disable constraint thủ công để che incident.
- Không force-push/rewrite history để bỏ commit người khác.

### Dừng và báo owner khi

- Source Jira/SRS/code thay đổi business semantics chưa được duyệt.
- Danh mục vùng và mapping tour cũ chưa được data owner xác nhận; giá cũ có phần lẻ; schedule timezone/counter writer chưa xác minh.
- Không có SQL-enabled environment hoặc required SQL tests skip.
- Figma/UC-24 design chưa truy cập/duyệt; không tự lấy frame TM-58 thay.
- UC-26 route chưa có cho nghiệm thu tích hợp.
- Cần thay auth/global schema/branch ngoài scope hoặc ghi dữ liệu thật chưa được cho phép.

Không dừng chỉ vì một tool lỗi nếu còn kiểm tra read-only an toàn khác; nhưng không tự cấp quyền nghiệp vụ hoặc giả kết quả để vượt blocker.

## 14. Bằng chứng triển khai cục bộ và bước tiếp theo

1. **Đã hoàn tất:** owner duyệt D0–D8 và kế hoạch Phase 1 ngày 15/09/2026.
2. **Code hiện có ngày 20/09/2026:** full schema + migration TM-70; Tour/TourSchedule/Destination/TourDestination EF mapping; public `GET /api/v1/tours` lọc bất kỳ vùng đã gắn và trả `destinations[]`; validation/contract/OpenAPI; SQL, endpoint và unit tests. Worktree `Capstone_BE_tm70`, branch `feature/datmnt-search-tours`, base `86516e2a85394341e6fad45bc78e3899c363ead6`; tất cả còn uncommitted.
3. **Bằng chứng hiện tại cho D1 nhiều vùng:** Release build 0 warning/0 error; full SQL-enabled suite trên SQL Server 2022 container test riêng: 7 Infrastructure + 184 Application + 92 API = **283 pass, 0 fail, 0 skip**. SQL-only suite: **28 pass, 0 fail, 0 skip**. SQL/API targeted tests: **31 pass, 0 fail, 0 skip**. Scoped whitespace format pass. NuGet vulnerability scan (direct + transitive): không phát hiện package có lỗ hổng từ nguồn hiện tại. Bằng chứng cũ 281/26 của D1 một chuỗi không còn dùng để nghiệm thu head mới. Đây là local evidence, không thay thế CI trên PR head.
4. **Đang chốt:** diff và data handoff; nếu source/test đổi sau số liệu trên, chạy lại gate bị ảnh hưởng và ghi counts mới.
5. **Gate còn mở:** data owner xác minh/backfill danh mục vùng và mapping từng tour, dữ liệu `base_price` phần lẻ, convention UTC của lịch cũ và writer `reserved_capacity`; dry run migration trên bản sao DB được phép; benchmark AC-31 trên dataset/tải đại diện; chạy CI ở head cuối khi được phép push. Không tự sửa dữ liệu thật hoặc ghi “availability chính xác” trước khi xác minh counter.
6. Chỉ sau khi Phase 1 đạt các gate release tương ứng mới bắt đầu G1 và Web/Mobile Phase 2; nếu muốn review code sớm, mô tả rõ các gate data/performance còn pending.

**Trạng thái:** code Database + Backend cho D1 nhiều vùng đã qua local full SQL-enabled suite; chưa áp migration lên DB cloud hoặc backfill dữ liệu vùng thật. Chưa commit, push, tạo PR, merge hay cập nhật Jira; Web/Mobile để Phase 2.
