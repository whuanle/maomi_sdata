using SData;

var builder = WebApplication.CreateBuilder(args);
builder.UseSData();

var app = builder.Build();
app.UseSDataPipeline();
app.Run();
