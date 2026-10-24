using Gamex.UserLibrary.Application.Catalog;
using Gamex.UserLibrary.Application.Library;
using Gamex.UserLibrary.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Gamex.UserLibrary.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<IListingService, ListingService>();
        services.AddScoped<IListingExpiryService, ListingExpiryService>();
        services.AddScoped<ICatalogReplicaService, CatalogReplicaService>();
        return services;
    }
}
