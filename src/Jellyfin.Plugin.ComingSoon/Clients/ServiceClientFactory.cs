using System.Net.Http;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.ComingSoon.Clients;

/// <summary>Creates service clients; swapped for fixture-backed clients in tests.</summary>
public interface IServiceClientFactory
{
    IRadarrClient CreateRadarr(ServiceEndpoint endpoint);

    ISonarrClient CreateSonarr(ServiceEndpoint endpoint);

    ISeerrClient CreateSeerr(ServiceEndpoint endpoint);
}

/// <summary>Production factory using Jellyfin's <see cref="IHttpClientFactory"/>.</summary>
public sealed class ServiceClientFactory(IHttpClientFactory httpClientFactory) : IServiceClientFactory
{
    public IRadarrClient CreateRadarr(ServiceEndpoint endpoint) => new RadarrClient(httpClientFactory.CreateClient(NamedClient.Default), endpoint);

    public ISonarrClient CreateSonarr(ServiceEndpoint endpoint) => new SonarrClient(httpClientFactory.CreateClient(NamedClient.Default), endpoint);

    public ISeerrClient CreateSeerr(ServiceEndpoint endpoint) => new SeerrClient(httpClientFactory.CreateClient(NamedClient.Default), endpoint);
}
