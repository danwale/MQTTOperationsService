Param(
	[Parameter(Mandatory = $true, Position = 1)]
    [ValidateNotNullOrEmpty()]
    [string]
    $ServerName = "DefaultNameIfNotProvided"
)
Write-Host "Script ran with param ServerName param value '$ServerName'"