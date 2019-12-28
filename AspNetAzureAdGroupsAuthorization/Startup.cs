using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using AspNetAzureAdGroupsAutorization.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.AzureAD.UI;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AspNetAzureAdGroupsAutorization
{
    public class Startup
    {
        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddAuthentication(AzureADDefaults.AuthenticationScheme)
                .AddAzureAD(options => Configuration.Bind("AzureAD", options));

            services.Configure<OpenIdConnectOptions>(AzureADDefaults.OpenIdScheme, options =>
            {
                options.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var roleGroups = new Dictionary<string, string>();
                        Configuration.Bind("AuthorizationGroups", roleGroups);

                        var graphService = await GraphService.CreateOnBehalfOfUserAsync(ctx.SecurityToken.RawData, Configuration);
                        var memberGroups = await graphService.CheckMemberGroupsAsync(roleGroups.Keys);

                        var claims = memberGroups.Select(groupGuid => new Claim(ClaimTypes.Role, roleGroups[groupGuid]));
                        var appIdentity = new ClaimsIdentity(claims);
                        ctx.Principal.AddIdentity(appIdentity);
                    }
                };
            });

            services.Configure<CookieAuthenticationOptions>(AzureADDefaults.CookieScheme, options => options.AccessDeniedPath = "/accessdenied");
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", async context =>
                {
                    await context.Response.WriteAsync("Im authenticated (no role required).");
                }).RequireAuthorization();

                endpoints.MapGet("/roletest", async context =>
                {
                    await context.Response.WriteAsync("You passed the role test!");
                }).RequireAuthorization(new AuthorizeAttribute() { Roles = "examplerole1" });

                endpoints.MapGet("/accessdenied", async context =>
                {
                    await context.Response.WriteAsync("Access denied!");
                });
            });
        }
    }
}
