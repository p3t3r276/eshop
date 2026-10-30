using AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var pg = builder.AddPostgres("pg")
    .WithDataVolume("shoplab-pg")
    .WithPgAdmin();

var keycloakAdmin = builder.AddParameter("keycloak-admin", "admin");
var keycloakPassword = builder.AddParameter("keycloak-password", "admin", secret: true);
var keycloak = builder.AddKeycloak("keycloak", 8080, keycloakAdmin, keycloakPassword)
    .WithDataVolume("shoplab-keycloak")
    .WithRealmImport("../../docker/keycloak");

var catalog = builder.AddProject<Projects.Catalog_Api>("catalog")
    .WithKeycloak(keycloak);

var gateway = builder.AddProject<Projects.Gateway_Web>("gateway")
    .WithKeycloak(keycloak)
    .WithReference(catalog)
    .WithExternalHttpEndpoints();

builder.Build().Run();
