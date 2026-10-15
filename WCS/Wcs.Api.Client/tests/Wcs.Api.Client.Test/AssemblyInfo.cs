using Wcs.Host.Test;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Wcs.Api.Client.Test;

[CollectionDefinition("host")]
public sealed class HostCollection : ICollectionFixture<HostFixture>;
