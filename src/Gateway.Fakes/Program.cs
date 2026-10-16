// Fake provider servers (Anthropic, OpenAI) for tests and end-to-end runs. Implemented in a later work item.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Gateway fakes");

app.Run();
