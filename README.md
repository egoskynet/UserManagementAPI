# UserManagementAPI

Minimal ASP.NET Core minimal API for managing users (in-memory store).

## Requirements

- .NET 10 SDK

## Run

Restore and run the project:

```sh
dotnet restore
dotnet run
```

The app listens on the addresses configured in [Properties/launchSettings.json](Properties/launchSettings.json). In Development the HTTP URL includes port 5113.

## Authentication

Requests must include an `Authorization: Bearer <token>` header. Tokens come from the `ApiTokens` configuration key or the `API_TOKENS` environment variable. A default dev token is `dev-token-123`.

## Endpoints

- GET /users — list users (supports `page`, `pageSize`, `search`)
- GET /users/{id} — get user by id
- POST /users — create user
- PUT /users/{id} — update user
- DELETE /users/{id} — delete user

OpenAPI/Swagger is available at `/openapi` and `/swagger` in Development (see [Program.cs](Program.cs)).

## Example requests

Use the provided HTTP collection: [requests.http](requests.http)

## Relevant files & symbols

- Project file: [UserManagementAPI.csproj](UserManagementAPI.csproj)
- Main app & endpoints: [Program.cs](Program.cs)
- Domain / DTOs: [`User`](Program.cs), [`CreateUserRequest`](Program.cs), [`UpdateUserRequest`](Program.cs)

## Notes

- Data is stored in-memory (a `ConcurrentDictionary`) and is not persisted across restarts.
- Email validation and basic request validation are implemented in [Program.cs](Program.cs).
