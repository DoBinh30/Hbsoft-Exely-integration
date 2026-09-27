using System.Text;
using System.Xml.Linq;

namespace ExelyPmsConnectDemo;

internal static class Program
{
    // Có thể thay các giá trị này bằng biến môi trường EXELY_* khi đưa lên production.
    private const string Endpoint = "https://pmsconnect.test.hopenapi.com/api/PMSConnect.svc?HotelCode=501661";
    private const string Username = "";
    private const string Password = "";
    private const string HotelCode = "501661";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("EXELY_ENDPOINT") ?? Endpoint;
            var username = Environment.GetEnvironmentVariable("EXELY_USERNAME") ?? Username;
            var password = Environment.GetEnvironmentVariable("EXELY_PASSWORD") ?? Password;
            var hotelCode = Environment.GetEnvironmentVariable("EXELY_HOTEL_CODE") ?? HotelCode;

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var exely = new ExelyPmsConnectClient(
                httpClient,
                new Uri(endpoint, UriKind.Absolute),
                username,
                password);

            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "read";
            if (command == "read")
            {
                var rawResponse = await exely.GetUndeliveredBookingsAsync(hotelCode);
                Console.WriteLine(rawResponse);
                return 0;
            }

            if (command == "confirm")
            {
                if (args.Length is < 5 or > 7)
                {
                    Console.Error.WriteLine(
                        "Cú pháp: confirm <EXELY_ID> <PMS_CREATE_TIME> <LAST_MODIFY_TIME> " +
                        "<PMS_ID> [ID_CONTEXT] [ROOM_STAYS_CSV]");
                    return 2;
                }

                var idContext = args.Length >= 6 && !string.IsNullOrWhiteSpace(args[5])
                    ? args[5]
                    : null;
                var roomStayIndexes = args.Length == 7
                    ? args[6].Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : Array.Empty<string>();

                var rawResponse = await exely.ConfirmBookingAsync(
                    hotelCode,
                    args[1],
                    args[2],
                    args[3],
                    args[4],
                    idContext,
                    roomStayIndexes);
                Console.WriteLine(rawResponse);
                return 0;
            }

            Console.Error.WriteLine("Lệnh hợp lệ: read hoặc confirm.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

/// <summary>
/// Client PMSConnect tối giản. Chỉ có hai hàm chức năng công khai và luôn trả về
/// nguyên văn response body của Exely, không parse hoặc thay đổi nội dung.
/// </summary>
public sealed class ExelyPmsConnectClient
{
    private const string ProtocolVersion = "1.18";
    private const string ReadSoapAction =
        "https://www.hopenapi.com/Api/PMSConnect/HotelReadReservationRQ";
    private const string ConfirmSoapAction =
        "https://www.hopenapi.com/Api/PMSConnect/NotifReportRQRequest";

    private static readonly XNamespace Soap =
        "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Security =
        "https://www.hopenapi.com/Api/PMSConnect";
    private static readonly XNamespace Ota =
        "http://www.opentravel.org/OTA/2003/05";

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _username;
    private readonly string _password;

    public ExelyPmsConnectClient(
        HttpClient httpClient,
        Uri endpoint,
        string username,
        string password)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _username = username;
        _password = password;
    }

    /// <summary>Lấy danh sách booking chưa được PMS xác nhận.</summary>
    public async Task<string> GetUndeliveredBookingsAsync(
        string hotelCode,
        CancellationToken cancellationToken = default)
    {
        var otaRequest = new XElement(
            Ota + "OTA_ReadRQ",
            new XAttribute("Version", ProtocolVersion),
            new XElement(
                Ota + "ReadRequests",
                new XElement(
                    Ota + "HotelReadRequest",
                    new XAttribute("HotelCode", hotelCode),
                    new XElement(
                        Ota + "SelectionCriteria",
                        new XAttribute("SelectionType", "Undelivered")))));

        return await PostSoapAsync(ReadSoapAction, otaRequest, cancellationToken);
    }

    /// <summary>Xác nhận booking đã được tạo thành công trong PMS.</summary>
    public async Task<string> ConfirmBookingAsync(
        string hotelCode,
        string channelBookingId,
        string pmsCreateDateTime,
        string lastModifyDateTime,
        string pmsBookingId,
        string? idContext = null,
        IReadOnlyCollection<string>? roomStayIndexNumbers = null,
        CancellationToken cancellationToken = default)
    {
        var uniqueId = new XElement(
            Ota + "UniqueID",
            new XAttribute("Type", "14"),
            new XAttribute("ID", channelBookingId));
        if (!string.IsNullOrWhiteSpace(idContext))
        {
            uniqueId.Add(new XAttribute("ID_Context", idContext));
        }

        XElement? roomStays = null;
        if (roomStayIndexNumbers is { Count: > 0 })
        {
            roomStays = new XElement(
                Ota + "RoomStays",
                roomStayIndexNumbers.Select(index =>
                    new XElement(
                        Ota + "RoomStay",
                        new XAttribute("IndexNumber", index))));
        }

        var reservation = new XElement(
            Ota + "HotelReservation",
            new XAttribute("CreateDateTime", pmsCreateDateTime),
            new XAttribute("LastModifyDateTime", lastModifyDateTime),
            new XAttribute("ResStatus", "Reserved"),
            uniqueId,
            roomStays,
            new XElement(
                Ota + "ResGlobalInfo",
                new XElement(
                    Ota + "HotelReservationIDs",
                    new XElement(
                        Ota + "HotelReservationID",
                        new XAttribute("ResID_Type", "14"),
                        new XAttribute("ResID_Value", pmsBookingId)))));

        var otaRequest = new XElement(
            Ota + "OTA_NotifReportRQ",
            new XAttribute("Version", ProtocolVersion),
            new XAttribute("EchoToken", Guid.NewGuid().ToString("N")),
            new XElement(Ota + "Success"),
            new XElement(
                Ota + "NotifDetails",
                new XAttribute("HotelCode", hotelCode),
                new XElement(
                    Ota + "HotelNotifReport",
                    new XElement(Ota + "HotelReservations", reservation))));

        return await PostSoapAsync(ConfirmSoapAction, otaRequest, cancellationToken);
    }

    private async Task<string> PostSoapAsync(
        string soapAction,
        XElement otaRequest,
        CancellationToken cancellationToken)
    {
        var envelope = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", Soap.NamespaceName),
                new XElement(
                    Soap + "Header",
                    new XAttribute(XNamespace.Xmlns + "pms", Security.NamespaceName),
                    new XElement(
                        Security + "Security",
                        new XAttribute("Username", _username),
                        new XAttribute("Password", _password))),
                new XElement(Soap + "Body", otaRequest)));

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{soapAction}\"");
        request.Content = new StringContent(
            envelope.ToString(SaveOptions.DisableFormatting),
            Encoding.UTF8,
            "text/xml");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
