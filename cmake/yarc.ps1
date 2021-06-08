function Export-Resource {
    [CmdletBinding()]
	param(
        [Parameter(Mandatory=$true,Position=0)]
        [string] $InputFile,
        [Parameter(Mandatory=$true,Position=1)]
        [string] $OutputFile,
        [Parameter(Mandatory=$true,Position=2)]
        [string] $SymbolName
	)

    $InputFile = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($InputFile)
    $OutputFile = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($OutputFile)

    $buffer = [System.IO.File]::ReadAllBytes($InputFile)
    $BufferSize = $buffer.Count

    $stream = [System.IO.File]::CreateText($OutputFile)
    $stream.WriteLine("const unsigned int ${SymbolName}_size = $BufferSize;")
    $stream.WriteLine("const unsigned char ${SymbolName}_data[$BufferSize] = {")

    for ($line = 0; $line -lt ([Math]::Floor($BufferSize / 16) - 1); $line++) {
        $slice = $buffer[($line * 16)..(($line * 16) + 15)]
        $row = (("0x{0:X2}, 0x{1:X2}, 0x{2:X2}, 0x{3:X2}, 0x{4:X2}, 0x{5:X2}, ") +
        ("0x{6:X2}, 0x{7:X2}, 0x{8:X2}, 0x{9:X2}, 0x{10:X2}, 0x{11:X2}, ") +
        ("0x{12:X2}, 0x{13:X2}, 0x{14:X2}, 0x{15:X2}, ")) -f $slice
        $stream.WriteLine($row)
    }
    $line++;

    $slice = $buffer[($line * 16)..($BufferSize - 1)]
    $row = ""
    foreach ($byte in $slice) {
        $row += "0x{0:X2}, " -f $byte
    }
    $row = $row.TrimEnd(", ")
    $stream.WriteLine($row)

    $stream.WriteLine("};")
    $stream.Close()
}

Export-Resource @args
