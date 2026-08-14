using FileSentry.Client;

namespace FileSentry.AspNetExample;

public static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        FileSentryClientOptions clientOptions =
            AspNetExampleConfiguration.FromConfiguration(builder.Configuration);
        clientOptions.Validate();

        builder.Services.AddSingleton(clientOptions);
        builder.Services
            .AddHttpClient<IFileSentryClient, FileSentryClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
        builder.Services.AddTransient<FileForwardingService>();
        builder.Services.AddProblemDetails();

        WebApplication app = builder.Build();
        app.UseExceptionHandler();
        app.MapGet("/", () => Results.Text(
            "POST one multipart file to /scan. Only clean content is returned."));
        app.MapPost("/scan", ScanEndpoint.HandleAsync);
        app.Run();
    }
}
