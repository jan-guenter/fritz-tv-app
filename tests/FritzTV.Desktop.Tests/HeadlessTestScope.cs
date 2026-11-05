using Xunit;

namespace FritzTV.Desktop.Tests;

[CollectionDefinition("Headless", DisableParallelization = true)]
public sealed class HeadlessTestScope : ICollectionFixture<HeadlessFixture>;
