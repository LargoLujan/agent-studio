using System;
using Sample.Core;
using static Sample.Core.Model.Thing;
using Alias = Sample.Núcleo.Scoped;

namespace Sample.App;

internal static class Program
{
    private static void Main() => Console.WriteLine(new Service());
}