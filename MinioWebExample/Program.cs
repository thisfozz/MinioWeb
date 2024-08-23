using Minio;

namespace MinioWebExample;
public class Program
{
    private const string Endpoint = "127.0.0.1:9000";
    private const string AccessKey = "BjRe99DyMQsqdJURzRiv";
    private const string SecretKey = "OGJGTKsZOu336fIxaXAXxSAIR4lI3GPQtz6q1RSq";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddMinio(client =>
        {
            client.WithEndpoint(Endpoint).WithCredentials(AccessKey, SecretKey).WithSSL(false).Build();
        });

        builder.Services.AddControllersWithViews();

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
        }
        else
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        app.UseStaticFiles();

        app.UseRouting();

        app.UseAuthorization();

        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");

        app.Run();
    }
}