// Importar espacios de nombres necesarios para red, entrada/salida, compresión y tareas asíncronas
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Text.Json;
using System.IO.Compression;
using System.Threading.Tasks; // Para ejecución concurrente

// Clase para deserializar la configuración del archivo config.json
class Config
{
    // Puerto donde escuchará el servidor
    public int port { get; set; }
    // Carpeta raíz desde donde se servirán los archivos web
    public string webRoot { get; set; }
}

class Program
{
    static void Main()
    {
        // 1. Leer configuración desde config.json para obtener puerto y carpeta raíz
        Config config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText("config.json"));
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("Error: El archivo config.json no se encontró.");
            return; // Terminar si no existe configuración
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Error en config.json: {ex.Message}");
            return; // Terminar si config.json está mal formado
        }

        int port = config.port;
        string webRoot = config.webRoot;

        // 2. Crear carpeta para logs si no existe
        Directory.CreateDirectory("logs");

        // 3. Verificar que la carpeta raíz exista
        if (!Directory.Exists(webRoot))
        {
            Console.WriteLine($"Error: carpeta '{webRoot}' no existe.");
            return;
        }

        // 4. Crear archivos HTML por defecto (index.html y 404.html) si no existen
        if (!File.Exists(Path.Combine(webRoot, "404.html")))
        {
            File.WriteAllText(Path.Combine(webRoot, "404.html"),
                "<!DOCTYPE html><html><head><title>404</title></head><body><h1>Error 404</h1><p>No encontrado.</p></body></html>");
        }

        if (!File.Exists(Path.Combine(webRoot, "index.html")))
        {
            File.WriteAllText(Path.Combine(webRoot, "index.html"),
                "<!DOCTYPE html><html><head><title>Bienvenido</title></head><body><h1>Servidor C#</h1><p>Inicio.</p></body></html>");
        }

        // 5. Iniciar servidor TCP escuchando en el puerto configurado
        TcpListener server = new TcpListener(IPAddress.Any, port);
        try
        {
            server.Start();
            Console.WriteLine($"Servidor escuchando en puerto {port}, sirviendo desde '{webRoot}'...");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Error al iniciar servidor: {ex.Message}");
            return;
        }

        // 6. Bucle infinito para aceptar conexiones y manejarlas concurrentemente
        while (true)
        {
            try
            {
                var client = server.AcceptTcpClient();
                // Atender cada cliente en una tarea separada para concurrencia
                _ = Task.Run(() => ManejarCliente(client, webRoot));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al aceptar cliente: {ex.Message}");
            }
        }
    }

    // Método que maneja una conexión cliente y procesa la solicitud HTTP
    static async Task ManejarCliente(TcpClient client, string webRoot)
    {
        string ipCliente = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();

        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            // Leer línea de solicitud HTTP (ejemplo: GET /index.html HTTP/1.1)
            string? requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine)) return;

            // Parsear método, ruta y versión de la solicitud
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
                Console.WriteLine($"Solicitud inválida de {ipCliente}: {requestLine}");
                return;
            }

            // Leer encabezados HTTP
            string? lineaHeader;
            int contentLength = 0;
            bool acceptGzip = false;

            while (!string.IsNullOrEmpty(lineaHeader = await reader.ReadLineAsync()))
            {
                if (lineaHeader.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(lineaHeader.Split(':')[1].Trim(), out contentLength);
                }
                else if (lineaHeader.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase))
                {
                    if (lineaHeader.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                    {
                        acceptGzip = true;
                    }
                }
            }

            // Leer cuerpo en caso de POST
            string body = "";
            if (metodo == "POST" && contentLength > 0)
            {
                char[] buffer = new char[contentLength];
                await reader.ReadBlockAsync(buffer, 0, contentLength);
                body = new string(buffer);
            }

            // Separar ruta y parámetros de consulta si existen
            string queryParams = "";
            string path = ruta;
            if (ruta.Contains("?"))
            {
                var partes = ruta.Split('?', 2);
                path = partes[0];
                queryParams = partes[1];
            }

            string archivoSolicitado = path == "/" ? "index.html" : path.TrimStart('/');
            string archivoCompleto = Path.Combine(webRoot, archivoSolicitado);

            string status, tipoContenido;
            byte[] datosRespuesta;

            // Verificar si el archivo existe para devolverlo
            if (File.Exists(archivoCompleto))
            {
                status = "200 OK";
                tipoContenido = ObtenerTipoContenido(archivoSolicitado);
                string contenido = await File.ReadAllTextAsync(archivoCompleto);

                // Comprimir con gzip si el cliente lo acepta
                if (acceptGzip)
                {
                    byte[] sinComprimir = Encoding.UTF8.GetBytes(contenido);
                    using var mem = new MemoryStream();
                    using (var gzip = new GZipStream(mem, CompressionLevel.Fastest, true))
                    {
                        await gzip.WriteAsync(sinComprimir, 0, sinComprimir.Length);
                    }
                    datosRespuesta = mem.ToArray();
                }
                else
                {
                    datosRespuesta = Encoding.UTF8.GetBytes(contenido);
                }
            }
            else
            {
                // Si no existe, devolver 404 personalizado
                status = "404 Not Found";
                tipoContenido = "text/html";
                string contenido404 = await File.ReadAllTextAsync(Path.Combine(webRoot, "404.html"));

                if (acceptGzip)
                {
                    byte[] sinComprimir = Encoding.UTF8.GetBytes(contenido404);
                    using var mem = new MemoryStream();
                    using (var gzip = new GZipStream(mem, CompressionLevel.Fastest, true))
                    {
                        await gzip.WriteAsync(sinComprimir, 0, sinComprimir.Length);
                    }
                    datosRespuesta = mem.ToArray();
                }
                else
                {
                    datosRespuesta = Encoding.UTF8.GetBytes(contenido404);
                }
            }

            // Construir encabezados HTTP de respuesta
            var responseHeaders = new StringBuilder();
            responseHeaders.Append($"HTTP/1.1 {status}\r\n");
            responseHeaders.Append($"Content-Type: {tipoContenido}\r\n");
            if (acceptGzip)
                responseHeaders.Append("Content-Encoding: gzip\r\n");
            responseHeaders.Append($"Content-Length: {datosRespuesta.Length}\r\n");
            responseHeaders.Append("\r\n"); // Línea en blanco que separa encabezados del cuerpo

            // Enviar encabezados y cuerpo
            await writer.WriteAsync(responseHeaders.ToString());
            await stream.WriteAsync(datosRespuesta, 0, datosRespuesta.Length);

            // Registrar petición en logs con información completa
            Loguear(ipCliente, metodo, archivoSolicitado, queryParams, body);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"E/S con cliente {ipCliente}: {ex.Message}");
            if (ex.InnerException is SocketException inner)
            {
                Console.WriteLine($"  SocketException: {inner.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inesperado con cliente {ipCliente}: {ex.Message}");
        }
        finally
        {
            client.Close(); // Cerrar conexión con cliente
        }
    }

    // Método para obtener el tipo MIME basado en la extensión del archivo
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

    // Método para escribir registros de acceso con detalles en archivos diarios de logs
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
