using System.ComponentModel;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// The Anvil's live connection indicator: polls <c>GET /api/health</c> and exposes the current
/// <see cref="ConnectionState"/> plus the last successful <see cref="HealthReportDto"/>.
/// </summary>
public interface IArcanumConnection : INotifyPropertyChanged
{

    ConnectionState State { get; }

    HealthReportDto? LastReport { get; }

    void Connect();

    void Disconnect();

}
