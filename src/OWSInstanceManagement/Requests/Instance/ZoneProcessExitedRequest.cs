using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.RequestPayloads;
using Serilog;
using System;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Instance
{
    /// <summary>
    /// The instance launcher reports that a zone server process it started has exited. Logs
    /// zone_process_exited, which the Grafana alert reads when Expected is false. An unexpected exit also gets a
    /// zone_exit AuditLog row, which the staff console shows on its Overview.
    /// </summary>
    public class ZoneProcessExitedRequest
    {
        public ZoneProcessExitedRequestPayload request { get; set; }

        private IInstanceManagementRepository _instanceManagementRepository;
        private Guid _customerGUID;
        private IAuditRepository _auditRepository;

        public void SetData(IInstanceManagementRepository instanceManagementRepository, IHeaderCustomerGUID customerGuid, IAuditRepository auditRepository)
        {
            _instanceManagementRepository = instanceManagementRepository;
            _customerGUID = customerGuid.CustomerGUID;
            _auditRepository = auditRepository;
        }

        public async Task<IActionResult> Handle()
        {
            //If the row can't be read we can't tell a deliberate stop from a crash, so report it as unexpected. The launcher
            //doesn't retry this call, so failing the request would lose the exit altogether.
            bool expected;
            try
            {
                GetServerInstanceFromPort instance = await _instanceManagementRepository.GetZoneInstance(_customerGUID, request.ZoneInstanceID);
                expected = IsExpectedExit(instance);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ZoneProcessExited could not read Zone Instance {ZoneInstanceId}; reporting its exit as unexpected", request.ZoneInstanceID);
                expected = false;
            }

            const string template = "zone_process_exited {ZoneInstanceId} {MapName}:{Port} exit code {ExitCode}, expected {Expected}";
            if (expected)
            {
                Log.Information(template, request.ZoneInstanceID, request.MapName, request.Port, request.ExitCode, expected);
            }
            else
            {
                Log.Warning(template, request.ZoneInstanceID, request.MapName, request.Port, request.ExitCode, expected);
                await _auditRepository.Add(_customerGUID, "instancemanagement", "launcher", "zone_exit", request.MapName,
                    new { zoneInstanceId = request.ZoneInstanceID, port = request.Port, exitCode = request.ExitCode });
            }

            return new OkObjectResult(new SuccessAndErrorMessage { Success = true, ErrorMessage = "" });
        }

        // Every deliberate stop marks the row first: ShutDownServerInstance sets status 3, and a launcher shutdown
        // or orphan cleanup deletes it (GetZoneInstance then returns an empty row).
        public static bool IsExpectedExit(GetServerInstanceFromPort instance)
        {
            return instance.MapInstanceID == 0 || instance.Status == 3;
        }
    }
}
