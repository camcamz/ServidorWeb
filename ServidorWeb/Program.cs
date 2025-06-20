using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Text.Json;
using System.IO.Compression;
using System.Threading.Tasks;

// Clase para deserializar la configuración desde config.json
class Config
{
    public int Port { get; set; }
    public required string webRoot { get; set; }
}

class Program
{
    static void Main()
    {
        // Leer configuración desde archivo externo
        Config config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText("config.json"));
        }
        catch
        {
            Console.WriteLine("Error al leer config.json");
            return;
        }

        int port = config.Port;
        string webRoot = config.webRoot;

        // Verificar que la carpeta de archivos exista
        if (!Directory.Exists(webRoot))
        {
            Console.WriteLine($"La carpeta {webRoot} no existe.");
            return;
        }

        Directory.CreateDirectory("logs"); // Crear carpeta de logs si no existe

        // Crear socket de servidor en puerto especificado
        var ipEndPoint = new IPEndPoint(IPAddress.Any, port);
        var serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            serverSocket.Bind(ipEndPoint);
            serverSocket.Listen(100); // Cola de hasta 100 conexiones pendientes
            Console.WriteLine($"Servidor escuchando en el puerto {port}, sirviendo desde '{webRoot}'...");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Error al iniciar servidor: {ex.Message}");
            return;
        }

        // Bucle principal: aceptar clientes continuamente
        while (true)
        {
            Socket clientSocket = serverSocket.Accept(); // Bloquea hasta que llegue una conexión
            _ = Task.Run(() => ManejarCliente(clientSocket, webRoot)); // Maneja cada cliente en una tarea aparte (concurrencia)
        }
    }

    // Función que maneja cada cliente
    static async Task ManejarCliente(Socket socket, string webRoot)
    {
        string ipCliente = ((IPEndPoint)socket.RemoteEndPoint!).Address.ToString();

        try
        {
            var buffer = new byte[8192];
            int recibido = await socket.ReceiveAsync(buffer, SocketFlags.None);
            if (recibido == 0) return;

            string request = Encoding.UTF8.GetString(buffer, 0, recibido);
            using var reader = new StringReader(request);

            // Leer la primera línea de la solicitud HTTP (ej: GET /index.html HTTP/1.1)
            string? requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine)) return;

            string metodo, ruta, version;
            try
            {
                var partes = requestLine.Split(' ');
                metodo = partes[0];
                ruta = partes[1];
                version = partes[2];
            }
            catch
            {
                return; // Si la solicitud está mal formada, se ignora
            }

            // Leer headers
            string? linea;
            int contentLength = 0;
            bool acceptGzip = false;
            while (!string.IsNullOrEmpty(linea = reader.ReadLine()))
            {
                if (linea.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(linea.Split(':')[1].Trim(), out contentLength);
                else if (linea.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase) && linea.Contains("gzip"))
                    acceptGzip = true;
            }

            // Leer cuerpo en caso de POST
            string body = "";
            if (metodo == "POST" && contentLength > 0)
            {
                char[] cuerpo = new char[contentLength];
                await reader.ReadBlockAsync(cuerpo, 0, contentLength);
                body = new string(cuerpo);
            }

            // Separar parámetros de consulta (si existen)
            string queryParams = "";
            string path = ruta;
            if (ruta.Contains("?"))
            {
                var partes = ruta.Split('?', 2);
                path = partes[0];
                queryParams = partes[1];
            }

            // Determinar qué archivo se está solicitando
            string archivoSolicitado = path == "/" ? "index.html" : path.TrimStart('/');
            string archivoCompleto = Path.Combine(webRoot, archivoSolicitado);

            string status, tipoContenido;
            byte[] datosRespuesta;

            if (File.Exists(archivoCompleto))
            {
                // Si el archivo existe, leerlo y preparar respuesta 200 OK
                status = "200 OK";
                tipoContenido = ObtenerTipoContenido(archivoSolicitado);
                string contenido = await File.ReadAllTextAsync(archivoCompleto);
                datosRespuesta = ComprimirSiEsNecesario(contenido, acceptGzip);
            }
            else
            {
                // Si no existe, enviar 404 y cargar archivo 404.html personalizado
                status = "404 Not Found";
                tipoContenido = "text/html";
                string contenido404 = await File.ReadAllTextAsync(Path.Combine(webRoot, "404.html"));
                datosRespuesta = ComprimirSiEsNecesario(contenido404, acceptGzip);
            }

            // Construir headers de respuesta
            var headers = new StringBuilder();
            headers.AppendLine($"HTTP/1.1 {status}");
            headers.AppendLine($"Content-Type: {tipoContenido}");
            if (acceptGzip) headers.AppendLine("Content-Encoding: gzip");
            headers.AppendLine($"Content-Length: {datosRespuesta.Length}");
            headers.AppendLine("Connection: close");
            headers.AppendLine();

            // Enviar headers y luego el contenido
            byte[] headersBytes = Encoding.UTF8.GetBytes(headers.ToString());
            await socket.SendAsync(headersBytes);
            await socket.SendAsync(datosRespuesta);

            // Registrar solicitud en logs
            Loguear(ipCliente, metodo, archivoSolicitado, queryParams, body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error con cliente {ipCliente}: {ex.Message}");
        }
        finally
        {
            socket.Close(); // Cerrar la conexión al terminar
        }
    }

    // Devuelve los bytes comprimidos si el cliente lo permite
    static byte[] ComprimirSiEsNecesario(string contenido, bool usarGzip)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(contenido);
        if (!usarGzip) return bytes;

        using var mem = new MemoryStream();
        using (var gzip = new GZipStream(mem, CompressionLevel.Fastest, true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }
        return mem.ToArray();
    }

    // Devuelve el tipo MIME del archivo solicitado
    static string ObtenerTipoContenido(string archivo)
    {
        string ext = Path.GetExtension(archivo).ToLower();
        return ext switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".txt" => "text/plain",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }

    // Guarda los datos de cada solicitud en un archivo de log diario
    static void Loguear(string ip, string metodo, string archivo, string query, string body)
    {
        string fecha = DateTime.Now.ToString("yyyy-MM-dd");
        string hora = DateTime.Now.ToString("HH:mm:ss");
        string logPath = Path.Combine("logs", $"{fecha}.log");

        string linea = $"{hora} | IP: {ip} | Método: {metodo} | Archivo: {archivo}";
        if (!string.IsNullOrEmpty(query))
            linea += $" | Query: {Uri.UnescapeDataString(query)}";
        if (!string.IsNullOrEmpty(body))
            linea += $" | Body: {body}";

        try
        {
            File.AppendAllText(logPath, linea + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al escribir log: {ex.Message}");
        }
    }
}
