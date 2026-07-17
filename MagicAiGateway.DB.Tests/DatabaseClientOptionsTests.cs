using MagicAiGateway.DB.Client.Configuration;
using MagicAiGateway.DB.Contracts;

namespace MagicAiGateway.DB.Tests;

public sealed class DatabaseClientOptionsTests
{
    [Fact]
    public void UnknownApplicationIsRejected()
    {
        var options = new DatabaseApiClientOptions();
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ConfiguredApplicationIsValid()
    {
        var options = new DatabaseApiClientOptions
        {
            Application = MagicApplication.PrimaryApi,
            EndpointOverride = new Uri("https://database.example.com")
        };

        var exception = Record.Exception(options.Validate);
        Assert.Null(exception);
    }
}
