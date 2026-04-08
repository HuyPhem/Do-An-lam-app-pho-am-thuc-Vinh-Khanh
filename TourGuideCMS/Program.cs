using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using TourGuideCMS.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
    });

builder.Services.AddAuthorization();
builder.Services.AddRazorPages();
builder.Services.AddSingleton<PlaceRepository>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// API JSON cho app MAUI (PostgREST-style): cấu hình URL trong app trỏ tới https://.../api/places
app.MapGet("/api/places", async (PlaceRepository repo) =>
{
    var rows = await repo.ListAsync();
    return Results.Json(rows, new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
});

app.MapRazorPages();

app.Run();
