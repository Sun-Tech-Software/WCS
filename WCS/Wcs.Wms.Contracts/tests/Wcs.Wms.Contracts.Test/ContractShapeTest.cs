using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Wms.Contracts.Test;

public class ContractShapeTest
{
    [Fact]
    public void EveryOperationIsAsynchronousAndCancellable()
    {
        var methods = typeof(IWarehouse).GetMethods();

        Assert.NotEmpty(methods);
        Assert.All(methods, m =>
            Assert.StartsWith("System.Threading.Tasks.Task", m.ReturnType.FullName));
        Assert.All(methods, m =>
            Assert.Equal(typeof(CancellationToken), m.GetParameters()[^1].ParameterType));
    }

    [Fact]
    public void TheUnavailableExceptionCarriesTheOperationThatFailed()
    {
        var ex = new WarehouseUnavailableException("DescribeCell", new TimeoutException());

        Assert.Equal("DescribeCell", ex.Operation);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }
}
