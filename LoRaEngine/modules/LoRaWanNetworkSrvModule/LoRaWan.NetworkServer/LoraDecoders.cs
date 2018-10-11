﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace LoRaWan.NetworkServer
{


    class LoraDecoders
    {
        public static string DecodeMessage(byte[] payload, uint fport, string SensorDecoder)
        {
            Type decoderType = typeof(LoraDecoders);
            MethodInfo toInvoke = decoderType.GetMethod(
               SensorDecoder, BindingFlags.Static | BindingFlags.NonPublic);

            if (toInvoke != null)
            {
       
                return (string)toInvoke.Invoke(null, new object[] { payload, fport});
            }
            else
            {
                var base64Payload = Convert.ToBase64String(payload);
                return $"{{\"error\": \"No '{SensorDecoder}' decoder found\", \"rawpayload\": \"{base64Payload}\"}}";
            }
        }
               
        private static string DecoderGpsSensor(byte[] payload, uint fport)
        {
            var result = Encoding.ASCII.GetString(payload);
            string[] values = result.Split(':');
            return String.Format("{{\"latitude\": {0} , \"longitude\": {1}}}", values[0], values[1]);
        }
        
        private static string DecoderTemperatureSensor(byte[] payload, uint fport)
        {
            var result = Encoding.ASCII.GetString(payload);
            return String.Format("{{\"temperature\": {0}}}", result);
        }

        private static string DecoderValueSensor(byte[] payload, uint fport)
        {
            var result = Encoding.ASCII.GetString(payload);
            return String.Format("{{\"value\": {0}}}", result);

        }

        private static string DecoderSearchAndRescueGpsSensor(byte[] payload, uint fport)
        {
            dynamic decoded = new JObject();


            if (payload.Length == 2)    /* receiving ONLY ONE delta */
            {
                var lat_val = (float)payload[0] - 127; //positive or negative?
                var lng_val = (float)payload[1] - 127;

                decoded.lat = lat_val / 10000;
                decoded.lng = lng_val / 10000;
            }
            else if (payload.Length == 8)     /* receiving full coordinates */
            {

                byte[] bytes_lat = { payload[0], payload[1], payload[2], payload[3] };
                byte[] bytes_lng = { payload[4], payload[5], payload[6], payload[7] };

                //default lora mote
                var lat_val = BitConverter.ToInt32(bytes_lat, 0);
                var lng_val = BitConverter.ToInt32(bytes_lng, 0);

                decoded.lat = (float)lat_val / Math.Pow(2, 23) * 90;
                decoded.lng = (float)lng_val / Math.Pow(2, 23) * 180;
            }
            else
            {
                return $"{{\"error\": \" length of '{payload.Length}' bytes is not supported, try 8 bytes for a complete GPS coordinate or 2 for a delta }}";
            }

            return decoded.ToString(Newtonsoft.Json.Formatting.None);

        }
    }

}
