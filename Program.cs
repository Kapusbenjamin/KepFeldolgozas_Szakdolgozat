using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Helpers;
using Kepfeldolgozas_Szakdolgozat.Services;
using Models;

namespace KepFeldolgozas_Szakdolgozat
{
    class Program
    {
        static void Main(string[] args)
        {
            MESService mesService = new MESService();

            try
            {
                var json = File.ReadAllText("input.json");

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                };

                var config = JsonSerializer.Deserialize<InputConfigModel>(json, options);

                double offsetX = 0.0;
                double offsetY = 0.0;
                double rotation = 0.0;

                var results = mesService.ImageProcessing(config.ImagePairs, config.Inspections, offsetX, offsetY, rotation);

                var outputJson = JsonSerializer.Serialize(results, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText("output.json", outputJson);
            }
            catch (JsonException)
            {
                Console.WriteLine("Unknown InspectionType! (Choose from these: ScrewTorque, Text, Barcode)");
            }
            catch (IOException)
            {
                Console.WriteLine("Input file not found or not readable!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error:");
                Console.WriteLine(ex.GetType());
                Console.WriteLine(ex.Message);
            }
        }
    }
}
