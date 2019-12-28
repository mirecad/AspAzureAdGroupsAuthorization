
# Authorize ASP.NET Core app by Azure AD groups using Graph API

Business websites use AD groups as authentication mechanism quite often. Before cloud era, ASP.NET translated AD groups into roles out of the box. This is no longer possible with Azure AD. At least not so simple. Now there are 2 ways you can check group membership:
* Set Azure AD to include security groups membership information into JWT token.
* Query Graph API for user groups.

There are many tutorials describing the first approach. It is easy and effective, however it has its limitations. If the user is member of a lot of groups, size of the token will grow. There is limit 200 group ids in one JWT token. Error message appears, that points you to Graph API, if you try to request token for user with more than 200 groups.
This sample demonstrates how to obtain users AD groups from Graph API and assign ASP.NET roles based on these groups. Roles are then stored in cookie, so only first request queries Graph API.



## How to run this sample

You need access to Azure AD to register your application and check ids of groups.

### Register Azure AD application
1. Create new Azure AD application and set its reply URL. I won't cover this in detail.
2. Set up a secret in *Certificates & secrets* tab.
3. In *API permissions* tab, add permission `Microsoft Graph` -> `GroupMember.Read.All`. `User.Read` is present by default. Don't forget to grant admin consent.

Fill in information about your app into `AzureAD` section of `appsettings.json` file.
```
"AzureAD": {
    "Instance": "https://login.microsoftonline.com/",
    "Domain": "<your domain>",
    "TenantId": "<your tenant id>",
    "ClientId": "<your client id>",
    "ClientSecret": "<your client secret>"
  },
```
You would want to place your secret somewhere safer in production application.

### Assign ASP.NET roles to your Azure AD groups
Find guid of your Azure AD groups. In the `AuthorizationGroups` section of `appsettings.json` file replace key-value pairs with group id as key and target role as value. You can add as many as you want.
```
  "AuthorizationGroups": {
    "5b99527f-947b-4e8d-aad5-404f8d39008c": "examplerole1",
    "2bd89580-1d95-4a9a-98c2-a7a150168cba": "examplerole2"
  },
```
### Set up endpoints authorization and run the application
There are 3 endpoints:
* `/` Default endpoint. Requires only to be logged in.
* `/roletest` Requires role to grant access.
* `/accessdenied` Redirect destination in case of failed authorization.

In `Startup.cs` modify `{ Roles = "examplerole1" }` to match one of roles specified in previous step.

```
app.UseEndpoints(endpoints =>
  {
      endpoints.MapGet("/", async context =>
      {
          await context.Response.WriteAsync("Im authorized (no required role).");
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
  ```

**Run the application.**

## How does it work

### Azure AD authentication
I used [Microsoft.AspNetCore.Authentication.AzureAD.UI](https://www.nuget.org/packages/Microsoft.AspNetCore.Authentication.AzureAD.UI/3.0.0) NuGet package. `Startup.cs` file changes:
```
services.AddAuthentication(AzureADDefaults.AuthenticationScheme)
    .AddAzureAD(options => Configuration.Bind("AzureAD", options));
```
```
app.UseAuthentication();
app.UseAuthorization();
```
This package takes care of setting up Open Id Connect and Cookies.

### Graph API
Class `GraphService.cs` takes care of all operations against Graph API. Method `CheckMemberGroupsAsync` gets collection of group ids and returns only ids, that user is member of. This is done by [CheckMemberGroups](https://docs.microsoft.com/en-us/graph/api/user-checkmembergroups?view=graph-rest-1.0&tabs=csharp) Graph API method.
```
public async Task<IEnumerable<string>> CheckMemberGroupsAsync(IEnumerable<string> groupIds)
{
    //You can check up to a maximum of 20 groups per request (see graph api doc).
    var batchSize = 20;

    var tasks = new List<Task<IDirectoryObjectCheckMemberGroupsCollectionPage>>();
    foreach (var groupsBatch in groupIds.Batch(batchSize))
    {
        tasks.Add(_client.Me.CheckMemberGroups(groupsBatch).Request().PostAsync());
    }
    await Task.WhenAll(tasks);

    return tasks.SelectMany(x => x.Result.ToList());
}
```
Information about which user groups to check is taken from user context. That's why `GraphServiceClient` must be created on behalf of user with it's token. I've create factory method `CreateOnBehalfOfUserAsync` for this purpose.
```
public static async Task<GraphService> CreateOnBehalfOfUserAsync(string userToken, IConfiguration configuration)
{
    var clientApp = ConfidentialClientApplicationBuilder
        .Create(configuration["AzureAD:ClientId"])
        .WithTenantId(configuration["AzureAD:TenantId"])
        .WithClientSecret(configuration["AzureAD:ClientSecret"])
        .Build();

    var authResult = await clientApp
        .AcquireTokenOnBehalfOf(new[] { "User.Read", "GroupMember.Read.All" }, new UserAssertion(userToken))
        .ExecuteAsync();

    GraphServiceClient graphClient = new GraphServiceClient(
        "https://graph.microsoft.com/v1.0",
        new DelegateAuthenticationProvider(async (requestMessage) =>
        {
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("bearer", authResult.AccessToken);
        }));

    return new GraphService(graphClient);
}
```
### Intercepting authentication flow and adding custom claims
OpenId exposes `OnTokenValidated` event. We can use returned token before authentication is finished. It is needed to create Graph API client, that will act on behalf of actual user. 
* Load key-value pairs of group ids and target roles from section `AuthorizationGroups` of configuration.
* Create Grap API service on behalf of actual user.
* Check which groups from configuration is user member of.
* Create role claims from returned entries.
* Add these claims to current user.

**Added claims are stored in cookie, so other requests do not trigger this event.**
```
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
   ```
