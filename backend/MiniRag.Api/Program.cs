using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using MiniRag.Api.Infrastructure;
using MiniRag.Api.Options;
using MiniRag.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.Section));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection(QdrantOptions.Section));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.Section));

builder.Services.AddControllers();

// Typed HTTP clients for the two backing services.
builder.Services.AddHttpClient<OllamaClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
});
builder.Services.AddHttpClient<QdrantVectorStore>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<QdrantOptions>>().Value;
    http.BaseAddress = new Uri(o.Url.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddSingleton<DocumentRegistry>();
builder.Services.AddSingleton<VectorIndexInitializer>();
builder.Services.AddSingleton<DocumentIndexingService>();
builder.Services.AddSingleton<DocumentTextExtractor>();
builder.Services.AddScoped<RagService>();
builder.Services.AddHostedService<StartupInitializer>();

// Upload size limit comes from configuration.
var maxUploadBytes = (builder.Configuration.GetValue<long?>("Rag:MaxUploadMb") ?? 50) * 1024 * 1024;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUploadBytes);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxUploadBytes);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.MapControllers();

app.Run();
