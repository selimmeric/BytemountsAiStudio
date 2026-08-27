using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Saglik ucu: hem insan hem izleme sistemi icin ilk temas noktasi.
// Ilerleyen fazlarda DB, kuyruk ve provider devre kesici durumlari buraya eklenecek.
app.MapGet("/health", () => Results.Ok(new HealthResponse(
    Status: "ok",
    Version: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
    Environment: app.Environment.EnvironmentName)));

app.Run();

internal sealed record HealthResponse(string Status, string Version, string Environment);
