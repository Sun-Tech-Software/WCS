using Microsoft.AspNetCore.SignalR;
using Wcs.Api.Contracts;

namespace Wcs.Host.Hubs;

/// <summary>Un solo hub, nessun gruppo: un host serve un impianto. I metodi li chiama il relay, non i client.</summary>
public sealed class PlantHub : Hub<IPlantEvents>;
