using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Text.Json;
using System.IO.Compression;
using System.Threading.Tasks; // Necesario para Task.Run

class Config
{
    public int port { get; set; }
    public string webRoot { get; set; }
}

class Program
{
    static void Main()
    {
        // 1. Cargar configuración del archivo config.json
        Config config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText("config.json"));
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("Error: El archivo config.json no se encontró. Asegúrate de que exista y esté en el directorio correcto.");
            return;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Error al parsear config.json: {ex.Message}. Asegúrate de que el formato sea válido.");
            return;
        }

        int port = config.port;
        string webRoot = config.webRoot;

        // Asegura que la carpeta de logs exista
        Directory.CreateDirectory("logs");

        // Asegura que la carpeta webRoot y el 404.html existan
        if (!Directory.Exists(webRoot))
        {
            Console.WriteLine($"Error: La carpeta webRoot '{webRoot}' no existe. Por favor, créala y coloca tus archivos web allí.");
            return;
        }
        if (!File.Exists(Path.Combine(webRoot, "404.html")))
        {
            Console.WriteLine($"Advertencia: No se encontró el archivo '404.html' en '{webRoot}'. Creando uno por defecto.");
            File.WriteAllText(Path.Combine(webRoot, "404.html"),
                "<!DOCTYPE html><html><head><title>404 Not Found</title></head><body><h1>Error 404</h1><p>La página que buscas no se encontró.</p></body></html>");
        }
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
        {
            Console.WriteLine($"Advertencia: No se encontró el archivo 'index.html' en '{webRoot}'. Creando uno por defecto.");
            File.WriteAllText(Path.Combine(webRoot, "index.html"),
                "<!DOCTYPE html><html><head><title>Welcome</title></head><body><h1>Bienvenido a tu servidor C#!</h1><p>Esta es la página por defecto.</p></body></html>");
        }


        // 2. Iniciar el servidor TCP
        TcpListener server = new TcpListener(IPAddress.Any, port);
        try
        {
            server.Start();
            Console.WriteLine($"Servidor escuchando en puerto {port}. Sirviendo archivos desde '{webRoot}'...");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Error al iniciar el servidor en puerto {port}: {ex.Message}. Asegúrate de que el puerto no esté en uso.");
            return;
        }


        // 3. Bucle principal para aceptar conexiones de clientes
        while (true)
        {
            try
            {
                var client = server.AcceptTcpClient();
                _ = Task.Run(() => ManejarCliente(client, webRoot)); // Atender concurrentemente
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al aceptar conexión de cliente: {ex.Message}");
            }
        }
    }

    static async Task ManejarCliente(TcpClient client, string webRoot) // Usar async Task para mejor manejo de flujos
    {
        string ipCliente = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            // 4. Leer la línea de solicitud HTTP
            string? requestLine = await reader.ReadLineAsync(); // Usar async para lectura no bloqueante
            if (string.IsNullOrEmpty(requestLine)) return;

            string metodo, ruta, version;
            try
            {
                var partes = requestLine.Split(' ');
                metodo = partes[0];
                ruta = partes[1];
                version = partes[2];
            }
            catch // Si la línea de solicitud no tiene el formato esperado
            {
                Console.WriteLine($"Solicitud inválida de {ipCliente}: {requestLine}");
                return;
            }

            // 5. Leer encabezados HTTP
            string? lineaHeader;
            int contentLength = 0;
            bool acceptGzip = false; // Bandera para la negociación de GZIP

            while (!string.IsNullOrEmpty(lineaHeader = await reader.ReadLineAsync())) // Leer encabezados
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

            // 6. Leer el cuerpo si es POST y tiene Content-Length
            string body = "";
            if (metodo == "POST" && contentLength > 0)
            {
                char[] buffer = new char[contentLength];
                // reader.ReadBlock(buffer, 0, contentLength); // ReadBlock no es async
                // Se puede usar ReadAsync para leer el cuerpo de forma asíncrona.
                // Para simplificar, si no hay problemas con bloques grandes síncronos, se deja así.
                // Para cuerpos POST muy grandes, se recomendaría una lectura más sofisticada.
                await reader.ReadBlockAsync(buffer, 0, contentLength);
                body = new string(buffer);
            }

            // 7. Parsear ruta y parámetros de consulta
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
            byte[] datosRespuesta; // Almacenará los datos finales (comprimidos o no)

            // 8. Determinar el contenido a servir
            if (File.Exists(archivoCompleto))
            {
                status = "200 OK";
                tipoContenido = ObtenerTipoContenido(archivoSolicitado);
                string contenidoTexto = await File.ReadAllTextAsync(archivoCompleto); // Lectura async

                if (acceptGzip)
                {
                    // Comprimir contenido
                    byte[] datosSinComprimir = Encoding.UTF8.GetBytes(contenidoTexto);
                    using (var memoria = new MemoryStream())
                    {
                        // Se usa CompressionLevel.Fastest para un equilibrio entre velocidad y tamaño
                        using (var gzip = new GZipStream(memoria, CompressionLevel.Fastest, true))
                        {
                            await gzip.WriteAsync(datosSinComprimir, 0, datosSinComprimir.Length);
                        } // Importante: GZipStream debe cerrarse para que los datos se vacíen en MemoryStream
                        datosRespuesta = memoria.ToArray();
                    }
                }
                else
                {
                    datosRespuesta = Encoding.UTF8.GetBytes(contenidoTexto);
                }
            }
            else // Archivo no encontrado
            {
                status = "404 Not Found";
                tipoContenido = "text/html";
                string contenido404 = await File.ReadAllTextAsync(Path.Combine(webRoot, "404.html"));

                // También se puede comprimir el 404 si el cliente lo acepta
                if (acceptGzip)
                {
                    byte[] datosSinComprimir = Encoding.UTF8.GetBytes(contenido404);
                    using (var memoria = new MemoryStream())
                    {
                        using (var gzip = new GZipStream(memoria, CompressionLevel.Fastest, true))
                        {
                            await gzip.WriteAsync(datosSinComprimir, 0, datosSinComprimir.Length);
                        }
                        datosRespuesta = memoria.ToArray();
                    }
                }
                else
                {
                    datosRespuesta = Encoding.UTF8.GetBytes(contenido404);
                }
            }

            // 9. Enviar respuesta HTTP al cliente
            var responseHeaders = new StringBuilder();
            responseHeaders.Append($"HTTP/1.1 {status}\r\n");
            responseHeaders.Append($"Content-Type: {tipoContenido}\r\n");
            if (acceptGzip) // Solo añadir Content-Encoding si se comprimió
            {
                responseHeaders.Append("Content-Encoding: gzip\r\n");
            }
            responseHeaders.Append($"Content-Length: {datosRespuesta.Length}\r\n");
            responseHeaders.Append("\r\n"); // Línea en blanco para separar encabezados del cuerpo

            await writer.WriteAsync(responseHeaders.ToString()); // Escribir encabezados
            await writer.FlushAsync(); // Asegurar que los encabezados se envíen

            // Escribir el cuerpo de la respuesta directamente al NetworkStream
            await stream.WriteAsync(datosRespuesta, 0, datosRespuesta.Length);
            await stream.FlushAsync(); // Asegurar que el cuerpo se envíe

            // 10. Loguear la solicitud
            Loguear(ipCliente, metodo, archivoSolicitado, queryParams, body);
        }
        catch (IOException ex)
        {
            // Captura la excepción de conexión anulada y la loguea para depuración
            Console.WriteLine($"Error de E/S con cliente {ipCliente}: {ex.Message}");
            // La excepción interna ya indica "conexión anulada"
            if (ex.InnerException is SocketException innerEx)
            {
                Console.WriteLine($"  Detalle SocketException: {innerEx.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inesperado al manejar cliente {ipCliente}: {ex.Message}");
        }
        finally
        {
            client.Close(); // Asegura que la conexión del cliente se cierre
        }
    }

    // 11. Función para obtener el tipo de contenido (MIME type)
    static string ObtenerTipoContenido(string archivo)
    {
        string ext = Path.GetExtension(archivo).ToLower();
        return ext switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json", // Añadir para JSON
            ".xml" => "application/xml",   // Añadir para XML si sirves
            ".txt" => "text/plain",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream" // Tipo por defecto para archivos desconocidos
        };
    }

    // 12. Función para loguear las solicitudes
    static void Loguear(string ip, string metodo, string archivo, string query, string body)
    {
        string fecha = DateTime.Now.ToString("yyyy-MM-dd");
        string hora = DateTime.Now.ToString("HH:mm:ss");
        string logPath = Path.Combine("logs", $"{fecha}.log");

        string linea = $"{hora} | IP: {ip} | Método: {metodo} | Archivo: {archivo}";

        if (!string.IsNullOrEmpty(query))
            linea += $" | Query: {Uri.UnescapeDataString(query)}"; // Decodificar query params para el log

        if (!string.IsNullOrEmpty(body))
            linea += $" | Body: {body}";

        try
        {
            File.AppendAllText(logPath, linea + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al escribir en el log {logPath}: {ex.Message}");
        }
    }
}