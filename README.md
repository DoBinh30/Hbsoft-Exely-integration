# Tích hợp PMS với Exely PMSConnect 1.18

Dự án này minh họa đúng hai chức năng cần thiết:

1. PMS lấy danh sách booking chưa xử lý từ Exely.
2. PMS thông báo lại cho Exely rằng booking đã được tạo thành công trong PMS.

Toàn bộ mã C# nằm trong một file `Program.cs`. Client không deserialize, không kiểm tra
`Success`, `Warnings` hay `Errors`; hai hàm API trả về nguyên chuỗi response body do Exely gửi.

## 1. Tổng quan luồng nghiệp vụ

```text
Khách đặt phòng trên Booking.com / Agoda / Expedia / website khách sạn
                                ↓
                  OTA chuyển booking sang Exely
                                ↓
              Exely giữ booking ở trạng thái Undelivered
                                ↓
         PMS gọi OTA_ReadRQ để lấy booking chưa xử lý
                                ↓
             Exely trả về OTA_ResRetrieveRS dạng XML
                                ↓
      PMS tự parse XML, kiểm tra và lưu booking vào database
                                ↓
       PMS gọi OTA_NotifReportRQ để xác nhận đã tạo booking
                                ↓
             Exely trả về OTA_NotifReportRS dạng XML
                                ↓
          PMS kiểm tra Success rồi mới cập nhật availability
```

PMS không gọi trực tiếp API của Booking.com hoặc Agoda. PMS gọi PMSConnect của Exely; Exely
là lớp trung gian giữa PMS và các OTA.

## 2. Hai hàm chính

### `GetUndeliveredBookingsAsync`

Gửi message `OTA_ReadRQ` với:

```xml
<SelectionCriteria SelectionType="Undelivered"/>
```

Hàm trả về nguyên SOAP XML của `OTA_ResRetrieveRS`:

```csharp
string rawXml = await exely.GetUndeliveredBookingsAsync(hotelCode);
```

### `ConfirmBookingAsync`

Gửi message `OTA_NotifReportRQ` với `ResStatus="Reserved"`, booking ID của Exely và booking
ID đã được PMS tạo. Hàm trả về nguyên SOAP XML của `OTA_NotifReportRS`:

```csharp
string rawXml = await exely.ConfirmBookingAsync(
    hotelCode,
    channelBookingId,
    pmsCreateDateTime,
    lastModifyDateTime,
    pmsBookingId,
    idContext,
    roomStayIndexNumbers);
```

Hai hàm không diễn giải response. Response có thể chứa `Success`, `Warnings`, `Errors` hoặc
SOAP fault; chương trình tích hợp chính chịu trách nhiệm xử lý chuỗi XML này.

## 3. Cấu hình kết nối

Bốn thông tin do Exely cung cấp:

- `Endpoint`: URL PMSConnect của môi trường test hoặc production.
- `Username`: tài khoản PMSConnect.
- `Password`: mật khẩu PMSConnect.
- `HotelCode`: mã khách sạn trên Exely.

Có thể điền trực tiếp ở đầu `Program.cs`. Khi triển khai thật, nên dùng biến môi trường:

```powershell
$env:EXELY_ENDPOINT = "https://ENDPOINT-DO-EXELY-CAP"
$env:EXELY_USERNAME = "USERNAME"
$env:EXELY_PASSWORD = "PASSWORD"
$env:EXELY_HOTEL_CODE = "HOTEL_CODE"
```

Không ghi username hoặc password vào log, source control hay nội dung exception gửi cho người
dùng cuối.

## 4. Chạy lấy booking

```powershell
dotnet run --project .\ExelyPmsConnectDemo\ExelyPmsConnectDemo.csproj -- read
```

Nếu không truyền lệnh, chương trình mặc định chạy `read`:

```powershell
dotnet run --project .\ExelyPmsConnectDemo\ExelyPmsConnectDemo.csproj
```

Console sẽ in đúng response body của Exely, ví dụ:

```xml
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
  <s:Body>
    <OTA_ResRetrieveRS xmlns="http://www.opentravel.org/OTA/2003/05" Version="1.18">
      <Success/>
      <ReservationsList>
        <!-- HotelReservation -->
      </ReservationsList>
    </OTA_ResRetrieveRS>
  </s:Body>
</s:Envelope>
```

Các trường hợp có thể nhận:

- Có `Success` và `ReservationsList`: có booking cần xử lý.
- Có `Success` nhưng không có `ReservationsList`: hiện không có booking mới.
- Có `Errors/Error`: Exely từ chối request; đọc `Code` và nội dung lỗi.
- HTTP lỗi hoặc SOAP fault: chuỗi body vẫn được trả về và in nguyên văn nếu server có body.
- Lỗi mạng, DNS hoặc timeout: `HttpClient` ném exception vì không có response body để trả về.

## 5. Dữ liệu PMS cần lấy từ booking

Client hiện tại cố ý không parse. Khi tích hợp vào PMS, tầng nghiệp vụ nên tự đọc ít nhất:

- `HotelReservation@CreateDateTime`.
- `HotelReservation@LastModifyDateTime`.
- `HotelReservation@ResStatus`.
- `UniqueID@ID`, `UniqueID@ID_Context`, `UniqueID@Type`.
- `RoomStay@IndexNumber`.
- Loại phòng, rate plan và ngày lưu trú.
- Số lượng người lớn, trẻ em và độ tuổi.
- Giá theo từng ngày và tổng tiền.
- Thông tin khách hàng.
- Dịch vụ bổ sung.
- Nguồn booking/OTA.
- Phương thức thanh toán, tiền đã nhận hoặc tiền dự kiến.
- Ghi chú và chính sách hủy.

Nếu response có nhiều `UniqueID`, khi xác nhận phải dùng ID `Type="14"` có `ID_Context` rỗng;
nếu không có thì dùng ID thuộc context `PMSConnect` theo dữ liệu Exely gửi.

`LastModifyDateTime` phải được lưu nguyên văn dưới dạng chuỗi. Không parse rồi format lại, không
đổi múi giờ và không làm mất phần mili giây.

## 6. Trình tự lưu booking trong PMS

Quy trình khuyến nghị cho từng `HotelReservation`:

1. Lấy `UniqueID` và `LastModifyDateTime` từ XML.
2. Kiểm tra booking/version đã tồn tại hay chưa.
3. Map room type, rate plan và service code của Exely sang mã PMS.
4. Validate ngày ở, khách, giá, tiền tệ và dữ liệu bắt buộc.
5. Mở transaction database.
6. Tạo booking mới hoặc cập nhật booking đã tồn tại.
7. Lưu quan hệ giữa Exely booking ID và PMS booking ID.
8. Commit transaction.
9. Chỉ sau khi commit thành công mới gọi `ConfirmBookingAsync`.
10. Parse raw response xác nhận và chỉ kết thúc khi có `OTA_NotifReportRS/Success`.

Khóa idempotency nên gồm tối thiểu:

```text
HotelCode + Exely UniqueID + LastModifyDateTime
```

Nhờ đó scheduler có thể nhận lại cùng một booking mà không tạo trùng trong PMS.

## 7. Chạy xác nhận booking

Chỉ xác nhận sau khi booking đã được ghi thành công vào PMS:

```powershell
dotnet run --project .\ExelyPmsConnectDemo\ExelyPmsConnectDemo.csproj -- `
  confirm `
  "EXELY_BOOKING_ID" `
  "PMS_CREATE_DATE_TIME" `
  "LAST_MODIFY_DATE_TIME_FROM_READ_RESPONSE" `
  "PMS_BOOKING_ID" `
  "ID_CONTEXT" `
  "111,222"
```

Ý nghĩa tham số:

| Vị trí | Tham số | Nguồn dữ liệu |
|---|---|---|
| 1 | `EXELY_BOOKING_ID` | `UniqueID@ID` nhận từ Exely |
| 2 | `PMS_CREATE_DATE_TIME` | Thời điểm PMS tạo booking, định dạng XML dateTime |
| 3 | `LAST_MODIFY_DATE_TIME` | Copy nguyên văn từ `HotelReservation@LastModifyDateTime` |
| 4 | `PMS_BOOKING_ID` | Mã booking do PMS tạo |
| 5 | `ID_CONTEXT` | Tùy chọn; lấy từ `UniqueID@ID_Context` |
| 6 | `ROOM_STAYS_CSV` | Tùy chọn; các `RoomStay@IndexNumber`, ngăn cách bằng dấu phẩy |

Nếu cần truyền room stay nhưng ID context rỗng, truyền chuỗi rỗng ở vị trí thứ năm:

```powershell
... "PMS_BOOKING_ID" "" "111,222"
```

Không đưa `RoomStay` vào xác nhận nếu toàn bộ booking tương ứng với một booking duy nhất trong
PMS. Dùng `RoomStay@IndexNumber` khi một booking Exely được tách thành nhiều booking PMS hoặc
khi cần xác nhận riêng từng stay.

## 8. Parse raw response trong chương trình chính

Việc parse nên nằm ngoài `ExelyPmsConnectClient`. Ví dụ tối thiểu:

```csharp
using System.Xml.Linq;

XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
XNamespace ota = "http://www.opentravel.org/OTA/2003/05";

var xml = XDocument.Parse(rawResponse, LoadOptions.PreserveWhitespace);
var body = xml.Root?.Element(soap + "Body");
var otaResponse = body?.Elements().SingleOrDefault();

var errors = otaResponse?
    .Element(ota + "Errors")?
    .Elements(ota + "Error")
    .Select(error => new
    {
        Code = (string?)error.Attribute("Code"),
        Message = error.Value.Trim()
    })
    .ToArray();

bool success = otaResponse?.Element(ota + "Success") is not null;
```

Đối với response đọc booking, lấy danh sách bằng:

```csharp
var reservations = otaResponse?
    .Element(ota + "ReservationsList")?
    .Elements(ota + "HotelReservation")
    .ToArray()
    ?? Array.Empty<XElement>();
```

## 9. Tích hợp vào ASP.NET Core hoặc Worker Service

Đăng ký một `HttpClient` dùng lại lâu dài; không tạo `HttpClient` mới cho mỗi booking:

```csharp
services.AddHttpClient("ExelyPmsConnect", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

services.AddSingleton(serviceProvider =>
{
    var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();

    return new ExelyPmsConnectClient(
        factory.CreateClient("ExelyPmsConnect"),
        new Uri(configuration["Exely:Endpoint"]!),
        configuration["Exely:Username"]!,
        configuration["Exely:Password"]!);
});
```

Worker chạy theo chu kỳ:

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    string rawXml = await exely.GetUndeliveredBookingsAsync(
        hotelCode,
        stoppingToken);

    // Parse XML -> lưu từng booking -> xác nhận từng booking thành công.

    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
}
```

Tài liệu giới hạn tối đa 30 `OTA_ReadRQ` mỗi khách sạn trong một giờ. Chu kỳ 5 phút tương đương
12 request/giờ và phù hợp cho vận hành thông thường.

## 10. Booking modification và cancellation

### Booking được sửa trên OTA

Modification có cùng `UniqueID` với booking cũ nhưng `LastModifyDateTime` mới. PMS phải cập nhật
booking hiện có, sau đó xác nhận lại đúng version vừa nhận.

```text
OTA sửa booking
  → Exely đưa version mới vào danh sách Undelivered
  → PMS đọc và cập nhật booking theo UniqueID
  → PMS xác nhận bằng LastModifyDateTime mới
```

### Khách hủy booking trên OTA

Exely chuyển trạng thái hủy về PMS trong dữ liệu booking. PMS cập nhật trạng thái nội bộ và xác
nhận đã xử lý. Cần kiểm tra `ResStatus` trước khi quyết định tạo, sửa hay hủy booking trong PMS.

### Khách sạn hủy booking từ PMS

Chiều PMS chủ động gửi hủy lên Exely dùng message `OTA_CancelRQ`. Chức năng này không nằm trong
hai hàm hiện tại.

## 11. Thứ tự cập nhật availability

Thứ tự bắt buộc:

```text
1. Lưu booking trong PMS
2. Gửi OTA_NotifReportRQ
3. Nhận OTA_NotifReportRS/Success
4. Cập nhật availability cho Exely
```

Không giảm availability trước khi xác nhận booking. Exely đang tạm giữ số phòng cho booking
Undelivered; cập nhật sớm có thể làm số phòng bị giảm hai lần.

## 12. Retry, log và bảo mật

- Booking mới cần được xử lý và xác nhận trong vòng 20 phút.
- Nếu lưu database thất bại, không gửi xác nhận.
- Nếu request đọc thất bại do mạng, retry có backoff và vẫn áp dụng idempotency khi nhận lại.
- Nếu gửi xác nhận nhưng mất kết nối trước khi nhận response, ghi nhận trạng thái chưa rõ ràng;
  kiểm tra lại trước khi retry liên tục.
- Ghi log thời gian, SOAPAction, HotelCode, HTTP status và correlation ID.
- Không ghi password hoặc SOAP security header vào log.
- Raw booking XML chứa dữ liệu cá nhân, thông tin lưu trú và có thể có dữ liệu thanh toán; mã hóa
  nơi lưu, giới hạn quyền truy cập và đặt thời gian lưu log phù hợp.
- Kiểm tra cả HTTP status lẫn `Success/Warnings/Errors` trong XML tại tầng nghiệp vụ.
- Luôn triển khai và chứng nhận trên môi trường test của Exely trước khi chuyển production.

## 13. Checklist production

- [ ] Endpoint, username, password và hotel code lấy từ secret/configuration.
- [ ] Scheduler chạy khoảng 5 phút một lần và không vượt 30 lần đọc/giờ.
- [ ] XML được lưu/parse mà không làm thay đổi `LastModifyDateTime`.
- [ ] Có mapping room type, rate plan, service và payment method.
- [ ] Có idempotency cho booking mới và modification.
- [ ] Chỉ xác nhận sau khi transaction PMS đã commit.
- [ ] Kiểm tra `OTA_NotifReportRS/Success` trước khi đánh dấu hoàn tất.
- [ ] Cập nhật availability sau xác nhận, không cập nhật trước.
- [ ] Có retry/backoff, monitoring và cảnh báo booking gần quá 20 phút.
- [ ] Raw XML và credentials được bảo vệ như dữ liệu nhạy cảm.
#   H b s o f t - E x e l y - i n t e g r a t i o n  
 #   H b s o f t - E x e l y - i n t e g r a t i o n  
 