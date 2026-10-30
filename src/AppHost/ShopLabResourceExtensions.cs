namespace AppHost;

public static class ShopLabResourceExtensions
{
    public static IResourceBuilder<ProjectResource> WithKeycloak(
        this IResourceBuilder<ProjectResource> project,IResourceBuilder<KeycloakResource> keycloak)
    {
        return project.WithEnvironment("Keycloak__Authority",
            ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/shoplab"))
            .WaitFor(keycloak);
    }
}
