using System.Text.Json;
using HubServiceCs;

public class JsonBodyTest
{
    [Fact]
    public void IntegersDecodeAsLongAndFractionsAsDouble()
    {
        using var document = JsonDocument.Parse("""{"i":1792500000,"f":1.5,"list":[2]}""");
        var value = (Dictionary<string, object?>)JsonBody.Decode(document.RootElement)!;
        Assert.IsType<long>(value["i"]);
        Assert.Equal(1792500000L, value["i"]);
        Assert.IsType<double>(value["f"]);
        Assert.IsType<long>(((List<object?>)value["list"]!)[0]);
    }
}
