using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using log4net;
using MySqlConnector;
using Teltonika.Codec;
using Teltonika.Codec.Model;

namespace TcpListener
{
    public class TcpClientService
    {
        private static readonly ILog Log = LogManager.GetLogger("");
        readonly TcpClient _client;

        public TcpClientService(TcpClient client)
        {
            _client = client;
        }

        public async Task Run()
        {
            var imei = "";

            using (_client)
            using (var stream = _client.GetStream())
            {
                Log.Info(DateTime.Now + " Received connection request from " + _client.Client.RemoteEndPoint);

                var fullPacket = new List<byte>();
                int? avlDataLength = null;

                var bytes = new byte[4096];
                var connected = false;
                int length;

                // Loop to receive all the data sent by the client.
                while ((length = await stream.ReadAsync(bytes, 0, bytes.Length)) != 0)
                {
                    Log.Info(string.Format("{0} - received [{1}]", DateTime.Now, String.Join("", bytes.Take(length).Select(x => x.ToString("X2")).ToArray())));

                    byte[] response;

                    if (!connected)
                    {
                        // Accept imei
                        imei = String.Join("", bytes.Take(length).Select(x => x.ToString("X2")).ToArray());
                        imei = HexAsciiConvert(imei.Substring(4, 30));

                        response = new byte[] { 01 };
                        connected = true;
                        await stream.WriteAsync(response, 0, response.Length);

                        Array.Clear(bytes, 0, bytes.Length);

                        Log.Info(string.Format("{0} - responded [{1}]", DateTime.Now, String.Join("", response.Select(x => x.ToString("X2")).ToArray())));
                    }
                    else
                    {
                        fullPacket.AddRange(bytes.Take(length));
                        Array.Clear(bytes, 0, bytes.Length);

                        var count = fullPacket.Count;

                        // continue if there is not enough bytes to get avl data array length
                        if (count < 8) continue;

                        avlDataLength = avlDataLength ?? BytesSwapper.Swap(BitConverter.ToInt32(fullPacket.GetRange(4, 4).ToArray(), 0));

                        var packetLength = 8 + avlDataLength + 4;
                        if (count > packetLength)
                        {
                            Log.Error("Too much data received.");
                            throw new ArgumentException("Too much data received.");
                        }
                        // continue if not all data received
                        if (count != packetLength) continue;

                        // Decode tcp packet
                        var decodedData = DecodeTcpPacket(fullPacket.ToArray());

                        sendData(imei, decodedData); // INSERTAMOS LOS DATOS EN LA BASE DE DATOS DE TRACKIN PARA QUALIMOVE

                        response = BitConverter.GetBytes(BytesSwapper.Swap(decodedData.AvlData.DataCount));

                        await stream.WriteAsync(response, 0, response.Length);

                        avlDataLength = null;
                        fullPacket.Clear();

                        Log.Info(string.Format("{0} - responded [{1}]", DateTime.Now, String.Join("", response.Select(x => x.ToString("X2")).ToArray())));
                    }
                }
            }
        }

        private async void sendData(string imei, TcpDataPacket data)
        {

            if (data.AvlData.Data != null)
            {
                foreach (var item in data.AvlData.Data)
                {

                    long ignition = -1, movement = -1, dataMode = -1, gsmSignal = -1, sleepMode = -1, tripOdometer = -1, odometer = -1, externalVoltage = -1;
                    long batteryVoltage = -1, batteryCurrent = -1;
                    long crashDetection = -1, manDown = -1, drivingState = -1, drivingRecords = -1, batteryLevel = -1, ble_temperature = -1;

                    Log.Info(string.Format("{0} - Fecha y Hora [{1}]", DateTime.Now, item.DateTime.ToLocalTime()));
                    Log.Info(string.Format("{0} - Latitud [{1}]", DateTime.Now, (long)item.GpsElement.Y));
                    Log.Info(string.Format("{0} - Longitud [{1}]", DateTime.Now, (long)item.GpsElement.X));
                    Log.Info(string.Format("{0} - Velocidad [{1}]", DateTime.Now, item.GpsElement.Speed));
                    Log.Info(string.Format("{0} - Altitud [{1}]", DateTime.Now, item.GpsElement.Altitude));
                    Log.Info(string.Format("{0} - Angle [{1}]", DateTime.Now, item.GpsElement.Angle));
                    Log.Info(string.Format("{0} - Satelites [{1}]", DateTime.Now, item.GpsElement.Satellites));

                    foreach (var io_element in item.IoElement.Properties)
                    {
                        switch (io_element.Id)
                        {

                            case 16:
                                odometer = (long)io_element.Value;
                                break;
                            case 21:
                                gsmSignal = (long)io_element.Value;
                                break;
                            case 25:
                                ble_temperature = (long)io_element.Value;
                                break;
                            case 66:
                                externalVoltage = (long)io_element.Value;
                                break;
                            case 67:
                                batteryVoltage = (long)io_element.Value;
                                break;
                            case 68:
                                batteryCurrent = (long)io_element.Value;
                                break;
                            case 80:
                                dataMode = (long)io_element.Value;
                                break;
                            case 113:
                                batteryLevel = (long)io_element.Value;
                                break;
                            case 199:
                                tripOdometer = (long)io_element.Value;
                                break;
                            case 200:
                                sleepMode = (long)io_element.Value;
                                break;
                            case 239:
                                ignition = (long)io_element.Value;
                                break;
                            case 240:
                                movement = (long)io_element.Value;
                                break;
                            case 242:
                                manDown = (long)io_element.Value;
                                break;
                            case 247:
                                crashDetection = (long)io_element.Value;
                                break;
                            case 283:
                                drivingState = (long)io_element.Value;
                                break;
                            case 284:
                                drivingRecords = (long)io_element.Value;
                                break;
                            default:

                                break;
                        }
                    }

                    try
                    {
                        Console.WriteLine("Connecting to MySQL...");
                        var connectionString = ConfigurationManager.ConnectionStrings["trackinDB-AUTOSTRADA"].ConnectionString;
                        using (MySqlConnection conn = new MySqlConnection(connectionString))
                        {
                            await conn.OpenAsync(); // Abrimos la conexion

                            string query = string.Format("INSERT INTO `registroGPS` " +
                            "(`imei`, `fecha`, `lat`, `lng`, `velocidad`, `altitud`, `rumbo`, `satelites`, `prioridad`, `io_ignition`, `io_movement`, `io_dataMode`, `io_GsmSignal`, `io_SleepMode`, `io_odometer`, `io_tripOdometer`, `io_batteryLevel`,`io_temperature`, `io_externalVoltage`,  `io_batteryVoltage`, `io_batteryCurrent`, `io_crashDetection`, `io_manDown`, `io_drivingState`, `io_drivingRecords`)  " +
                            " VALUES ('{0}', STR_TO_DATE('{1}','%d/%m/%Y %H:%i:%s' ), {2}, {3}, {4}, {5}, {6}, {7}, '{8}', {9}, {10}, {11}, {12}, {13}, {14}, {15}, {16}, {17}, {18}, {19}, {20}, {21}, {22}, {23}, {24}) ",
                            imei, item.DateTime.ToLocalTime(), (long)item.GpsElement.Y, (long)item.GpsElement.X, item.GpsElement.Speed, item.GpsElement.Altitude, item.GpsElement.Angle, item.GpsElement.Satellites, item.Priority,
                            ignition, movement, dataMode, gsmSignal, sleepMode, odometer, tripOdometer, batteryLevel, ble_temperature, externalVoltage, batteryVoltage, batteryCurrent, crashDetection, manDown, drivingState, drivingRecords);

                            using (MySqlCommand cmd = new MySqlCommand(query, conn))
                            {


                                Log.Info("query " + cmd.CommandText.ToString());

                                await cmd.ExecuteNonQueryAsync();
                            }

                            await conn.CloseAsync(); // Cerramos la conexion

                        }
                        Console.WriteLine("Done.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(string.Format("{0} - Error al conectar con la base de datos {1}", DateTime.Now, ex.ToString()));
                        
                    }
                }
            }
            else
            {
                Log.Info(string.Format("NO HAY DATOS EN AVL DATA"));
            }
        }

        private static string HexAsciiConvert(string hexString)
        {
            try
            {
                string ascii = string.Empty;

                for (int i = 0; i < hexString.Length; i += 2)
                {
                    string hs = string.Empty;

                    hs = hexString.Substring(i, 2);
                    long deccc = Convert.ToInt64(hs, 16);
                    char character = Convert.ToChar(deccc);

                    ascii += character;

                }

                return ascii;
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); }

            return string.Empty;
        }

        private static TcpDataPacket DecodeTcpPacket(byte[] request)
        {
            var reader = new ReverseBinaryReader(new MemoryStream(request));
            var decoder = new DataDecoder(reader);

            return decoder.DecodeTcpData();
        }
    }
}