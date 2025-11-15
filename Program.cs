using System;
using System.Collections.Concurrent;
using System.Net.Mail;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddLogging();

var app = builder.Build();

// --- Error handling middleware (must be first) ---
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled exception caught by middleware");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Internal server error." });
        }
    }
});

// --- Authentication middleware (must be second) ---
// Token(s) can be provided via configuration key "ApiTokens" (comma separated) or env "API_TOKENS".
// Falls back to a dev token when not set.
var tokensFromConfig = builder.Configuration["ApiTokens"] ??
                       Environment.GetEnvironmentVariable("API_TOKENS") ??
                       "dev-token-123";

var validTokens = new HashSet<string>(tokensFromConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

app.Use(async (context, next) =>
{
    // Allow OpenAPI/Swagger and health endpoints to be accessed without token in development
    var path = context.Request.Path.Value ?? string.Empty;
    if (app.Environment.IsDevelopment() && (path.StartsWith("/swagger") || path.StartsWith("/openapi") || path.StartsWith("/health")))
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("Authorization", out var auth) || string.IsNullOrWhiteSpace(auth))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
        return;
    }

    var header = auth.ToString();
    // Expect "Bearer <token>"
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
        return;
    }

    var token = header.Substring("Bearer ".Length).Trim();
    if (string.IsNullOrEmpty(token) || !validTokens.Contains(token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
        return;
    }

    // attach token (or minimal identity) to context for auditing if needed
    context.Items["callerToken"] = token;

    await next();
});

// --- Logging middleware (must be last of the three) ---
// Logs method, path, response status code, and caller token marker
app.Use(async (context, next) =>
{
    var sw = Stopwatch.StartNew();
    var method = context.Request.Method;
    var path = context.Request.Path + context.Request.QueryString;
    await next();
    sw.Stop();
    var status = context.Response?.StatusCode;
    var caller = context.Items.TryGetValue("callerToken", out var t) ? t?.ToString() : "anonymous";
    // mask token for audit logs (show last 4 chars)
    string masked = string.IsNullOrEmpty(caller) || caller == "anonymous" ? "anonymous" : (caller.Length <= 4 ? "****" : "****" + caller.Substring(caller.Length - 4));
    app.Logger.LogInformation("{Method} {Path} responded {StatusCode} in {Elapsed}ms caller={Caller}", method, path, status, sw.ElapsedMilliseconds, masked);
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// In-memory thread-safe store for users
var users = new ConcurrentDictionary<Guid, User>();

// Optional seed user
var seed = new User(Guid.NewGuid(), "Alice", "Smith", "alice@example.com", DateTime.UtcNow);
users[seed.Id] = seed;

// Helper: validate email
static bool IsValidEmail(string email)
{
    if (string.IsNullOrWhiteSpace(email)) return false;
    try
    {
        var _ = new MailAddress(email);
        return true;
    }
    catch
    {
        return false;
    }
}

// Helper: validate create request
(bool IsValid, string? Error) ValidateCreate(CreateUserRequest req, ConcurrentDictionary<Guid, User> users)
{
    if (req is null) return (false, "Request body is required.");
    if (string.IsNullOrWhiteSpace(req.FirstName) || string.IsNullOrWhiteSpace(req.LastName))
        return (false, "FirstName and LastName are required.");
    if (req.FirstName.Length > 100 || req.LastName.Length > 100)
        return (false, "FirstName and LastName must be 100 characters or fewer.");
    if (!IsValidEmail(req.Email))
        return (false, "Email is invalid.");
    if (users.Values.Any(u => string.Equals(u.Email, req.Email, StringComparison.OrdinalIgnoreCase)))
        return (false, "Email already in use.");
    return (true, null);
}

// Helper: validate update request
(bool IsValid, string? Error) ValidateUpdate(UpdateUserRequest req, ConcurrentDictionary<Guid, User> users, Guid id)
{
    if (req is null) return (false, "Request body is required.");
    if (req.FirstName != null && req.FirstName.Length > 100) return (false, "FirstName must be 100 characters or fewer.");
    if (req.LastName != null && req.LastName.Length > 100) return (false, "LastName must be 100 characters or fewer.");
    if (req.Email != null && !IsValidEmail(req.Email)) return (false, "Email is invalid.");
    if (req.Email != null && users.Values.Any(u => u.Id != id && string.Equals(u.Email, req.Email, StringComparison.OrdinalIgnoreCase)))
        return (false, "Email already in use.");
    return (true, null);
}

// GET /users - list all users (supports pagination & optional search)
app.MapGet("/users", (int? page, int? pageSize, string? search) =>
{
    try
    {
        // pagination defaults
        var p = page.GetValueOrDefault(1);
        var ps = pageSize.GetValueOrDefault(50);
        if (p < 1) p = 1;
        if (ps < 1) ps = 1;
        if (ps > 200) ps = 200; // cap to protect memory

        // Snapshot to avoid enumerating a changing collection repeatedly
        var snapshot = users.Values.ToArray();

        var query = snapshot.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(u =>
                u.FirstName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                u.LastName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                u.Email.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        var total = query.Count();
        var items = query.Skip((p - 1) * ps).Take(ps);

        return Results.Ok(new { page = p, pageSize = ps, total, items });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error in GET /users");
        return Results.Problem(detail: "Failed to retrieve users.", statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("GetUsers");

// GET /users/{id} - get user by id
app.MapGet("/users/{id:guid}", (Guid id) =>
{
    try
    {
        return users.TryGetValue(id, out var user)
            ? Results.Ok(user)
            : Results.NotFound(new { error = "User not found." });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error in GET /users/{id}", id);
        return Results.Problem(detail: "Failed to retrieve user.", statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("GetUserById");

// POST /users - create a new user
app.MapPost("/users", (CreateUserRequest req) =>
{
    try
    {
        var (isValid, error) = ValidateCreate(req, users);
        if (!isValid) return Results.BadRequest(new { error });

        var user = new User(Guid.NewGuid(), req.FirstName.Trim(), req.LastName.Trim(), req.Email.Trim(), DateTime.UtcNow);
        if (!users.TryAdd(user.Id, user))
        {
            app.Logger.LogWarning("Failed to add user with id {UserId}", user.Id);
            return Results.Problem(detail: "Failed to create user.", statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Created($"/users/{user.Id}", user);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error in POST /users");
        return Results.Problem(detail: "Failed to create user.", statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("CreateUser");

// PUT /users/{id} - update an existing user
app.MapPut("/users/{id:guid}", (Guid id, UpdateUserRequest req) =>
{
    try
    {
        if (!users.TryGetValue(id, out var existing))
            return Results.NotFound(new { error = "User not found." });

        var (isValid, error) = ValidateUpdate(req, users, id);
        if (!isValid) return Results.BadRequest(new { error });

        var updated = existing with
        {
            FirstName = string.IsNullOrWhiteSpace(req.FirstName) ? existing.FirstName : req.FirstName.Trim(),
            LastName  = string.IsNullOrWhiteSpace(req.LastName)  ? existing.LastName  : req.LastName.Trim(),
            Email     = string.IsNullOrWhiteSpace(req.Email)     ? existing.Email     : req.Email.Trim()
        };

        users[id] = updated;
        return Results.Ok(updated);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error in PUT /users/{id}", id);
        return Results.Problem(detail: "Failed to update user.", statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("UpdateUser");

// DELETE /users/{id} - remove a user
app.MapDelete("/users/{id:guid}", (Guid id) =>
{
    try
    {
        return users.TryRemove(id, out _)
            ? Results.NoContent()
            : Results.NotFound(new { error = "User not found." });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error in DELETE /users/{id}", id);
        return Results.Problem(detail: "Failed to delete user.", statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("DeleteUser");

app.Run();

// Domain + DTOs
record User(Guid Id, string FirstName, string LastName, string Email, DateTime CreatedAt);

record CreateUserRequest(string FirstName, string LastName, string Email);
record UpdateUserRequest(string? FirstName, string? LastName, string? Email);
