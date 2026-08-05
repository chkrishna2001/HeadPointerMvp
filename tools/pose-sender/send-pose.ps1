param(
    [double]$Yaw = 20,
    [double]$Pitch = 0,
    [double]$Roll = 0,
    [int]$Port = 11573,
    [int]$Count = 12,
    [int]$DelayMilliseconds = 100
)

$client = [System.Net.Sockets.UdpClient]::new()
$endpoint = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse("127.0.0.1"), $Port)

try {
    for ($i = 0; $i -lt $Count; $i++) {
        $json = '{{"yaw":{0},"pitch":{1},"roll":{2}}}' -f $Yaw, $Pitch, $Roll
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        [void]$client.Send($bytes, $bytes.Length, $endpoint)
        Start-Sleep -Milliseconds $DelayMilliseconds
    }
}
finally {
    $client.Dispose()
}
