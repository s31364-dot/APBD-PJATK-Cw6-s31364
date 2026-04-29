using Hospital.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using Hospital.DTO;
using CreateAppointmentRequestDto = Hospital.DTOs.CreateAppointmentRequestDto;

namespace Hospital.Controllers;

[ApiController]
[Route("api/appointments")]
public class AppointmentsController : ControllerBase
{
    public readonly string connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        connectionString = configuration.GetConnectionString("DefaultConnection")!;
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        var appointments = new List<AppointmentListDto>();
        await using var connection = new SqlConnection(connectionString);
        await using var command = new SqlCommand(@"
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason,
                   p.FirstName + ' ' + p.LastName AS PatientFullName, p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@LastName IS NULL OR p.LastName = @LastName)
            ORDER BY a.AppointmentDate", connection);

        command.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@LastName", (object?)patientLastName ?? DBNull.Value);

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            appointments.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32("IdAppointment"),
                AppointmentDate = reader.GetDateTime("AppointmentDate"),
                Status = reader.GetString("Status"),
                Reason = reader.GetString("Reason"),
                PatientFullName = reader.GetString("PatientFullName"),
                PatientEmail = reader.GetString("PatientEmail")
            });
        }
        return Ok(appointments);
    }

    [HttpGet("{idAppointment:int}")]
    public async Task<IActionResult> GetAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(connectionString);
        await using var command = new SqlCommand(@"
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes,
                   p.FirstName + ' ' + p.LastName AS PatientFullName, p.Email, p.PhoneNumber,
                   d.FirstName + ' ' + d.LastName AS DoctorFullName, d.LicenseNumber,
                   s.Name AS Specialization
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            JOIN dbo.Doctors d ON a.IdDoctor = d.IdDoctor
            JOIN dbo.Specializations s ON d.IdSpecialization = s.IdSpecialization
            WHERE a.IdAppointment = @Id", connection);

        command.Parameters.AddWithValue("@Id", idAppointment);
        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje." });

        return Ok(new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32("IdAppointment"),
            AppointmentDate = reader.GetDateTime("AppointmentDate"),
            Status = reader.GetString("Status"),
            Reason = reader.GetString("Reason"),
            InternalNotes = reader.IsDBNull("InternalNotes") ? null : reader.GetString("InternalNotes"),
            PatientFullName = reader.GetString("PatientFullName"),
            PatientEmail = reader.GetString("Email"),
            PatientPhoneNumber = reader.GetString("PhoneNumber"),
            DoctorFullName = reader.GetString("DoctorFullName"),
            DoctorLicenseNumber = reader.GetString("LicenseNumber"),
            SpecializationName = reader.GetString("Specialization")
        });
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment(CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate <= DateTime.Now)
            return BadRequest(new ErrorResponseDto { Message = "Termin nie może być в przeszłości." });
        
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
            return BadRequest(new ErrorResponseDto { Message = "Opis wizyty jest wymagany (max 250 znaków)." });

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        const string checkSql = @"
            SELECT (SELECT IsActive FROM Patients WHERE IdPatient = @IdP) AS PActive,
                   (SELECT IsActive FROM Doctors WHERE IdDoctor = @IdD) AS DActive,
                   (SELECT COUNT(*) FROM Appointments WHERE IdDoctor = @IdD AND AppointmentDate = @Date AND Status != 'Cancelled') AS Conflict";
        
        await using var checkCmd = new SqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("@IdP", request.IdPatient);
        checkCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
        checkCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);

        using (var reader = await checkCmd.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            if (reader.IsDBNull(0) || !reader.GetBoolean(0)) return BadRequest(new ErrorResponseDto { Message = "Pacjent nie istnieje lub jest nieaktywny." });
            if (reader.IsDBNull(1) || !reader.GetBoolean(1)) return BadRequest(new ErrorResponseDto { Message = "Lekarz nie istnieje lub jest nieaktywny." });
            if (reader.GetInt32(2) > 0) return Conflict(new ErrorResponseDto { Message = "Lekarz ma już inną wizytę w tym terminie." });
        }

        await using var insertCmd = new SqlCommand(@"
            INSERT INTO Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            VALUES (@IdP, @IdD, @Date, 'Scheduled', @Reason);
            SELECT SCOPE_IDENTITY();", connection);
        
        insertCmd.Parameters.AddWithValue("@IdP", request.IdPatient);
        insertCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
        insertCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
        insertCmd.Parameters.AddWithValue("@Reason", request.Reason);

        var newId = Convert.ToInt32(await insertCmd.ExecuteScalarAsync());
        return CreatedAtAction(nameof(GetAppointment), new { idAppointment = newId }, null);
    }

    [HttpPut("{idAppointment:int}")]
    public async Task<IActionResult> UpdateAppointment(int idAppointment, UpdateAppointmentRequestDto request)
    {
        
        var validStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
        if (!validStatuses.Contains(request.Status))
        {
            return BadRequest(new ErrorResponseDto { Message = "Nieprawidłowy status wizyty." });
        }
        
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var currentCmd = new SqlCommand("SELECT Status, AppointmentDate FROM Appointments WHERE IdAppointment = @Id", connection);
        currentCmd.Parameters.AddWithValue("@Id", idAppointment);

        using (var reader = await currentCmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje." });
            if (reader.GetString(0) == "Completed" && request.AppointmentDate != reader.GetDateTime(1))
                return BadRequest(new ErrorResponseDto { Message = "Nie można zmienić terminu zakończonej wizyty." });
        }

        await using var conflictCmd = new SqlCommand(@"
            SELECT COUNT(*) FROM Appointments 
            WHERE IdDoctor = @IdD AND AppointmentDate = @Date AND IdAppointment != @IdA AND Status != 'Cancelled'", connection);
        conflictCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
        conflictCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
        conflictCmd.Parameters.AddWithValue("@IdA", idAppointment);

        if ((int)await conflictCmd.ExecuteScalarAsync()! > 0)
            return Conflict(new ErrorResponseDto { Message = "Lekarz ma już zaplanowaną inną wizytę w tym terminie." });

        await using var updateCmd = new SqlCommand(@"
            UPDATE Appointments SET 
                IdPatient = @IdP, IdDoctor = @IdD, AppointmentDate = @Date, 
                Status = @Status, Reason = @Reason, InternalNotes = @Notes
            WHERE IdAppointment = @IdA", connection);
        
        updateCmd.Parameters.AddWithValue("@IdP", request.IdPatient);
        updateCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
        updateCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
        updateCmd.Parameters.AddWithValue("@Status", request.Status);
        updateCmd.Parameters.AddWithValue("@Reason", request.Reason);
        updateCmd.Parameters.AddWithValue("@Notes", (object?)request.InternalNotes ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@IdA", idAppointment);

        await updateCmd.ExecuteNonQueryAsync();
        return Ok();
    }

    [HttpDelete("{idAppointment:int}")]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var statusCmd = new SqlCommand("SELECT Status FROM Appointments WHERE IdAppointment = @Id", connection);
        statusCmd.Parameters.AddWithValue("@Id", idAppointment);
        var status = (await statusCmd.ExecuteScalarAsync())?.ToString();

        if (status == null) return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje." });
        if (status == "Completed") return Conflict(new ErrorResponseDto { Message = "Nie można usunąć wizyty o statusie Completed." });

        await using var deleteCmd = new SqlCommand("DELETE FROM Appointments WHERE IdAppointment = @Id", connection);
        deleteCmd.Parameters.AddWithValue("@Id", idAppointment);
        await deleteCmd.ExecuteNonQueryAsync();

        return NoContent();
    }
}