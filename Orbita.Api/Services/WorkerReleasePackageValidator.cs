using Orbita.Contracts;

namespace Orbita.Api.Services;

public static class WorkerReleasePackageValidator
{
    public static bool LooksLikeMsi(string path) => WorkerMsiPackage.LooksLikeMsi(path);
}