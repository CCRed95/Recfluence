﻿using System;
using System.Net;
using System.Net.Http;

namespace SysExtensions.Net {
  public static class HttpExtensions {
    public static UriBuilder Build(this Uri uri) => new(uri);
    public static Uri AsUri(this string url) => new(url);

    public static void EnsureSuccess(int status, string url) {
      if (!IsSuccess(status)) throw new HttpRequestException($"{url} failed with  '{status}'", null, (HttpStatusCode) status);
    }

    public static void EnsureSuccess(this HttpStatusCode status, string url) => EnsureSuccess((int) status, url);
    public static bool IsSuccess(int code) => code >= 200 && code <= 299;
    public static bool IsSuccess(this HttpStatusCode code) => IsSuccess((int) code);

    public static bool IsTransient(int code) => code switch {
      < 500 => code == 408,
      _ => true
    };

    public static bool IsTransient(this HttpStatusCode code) => IsTransient((int) code);
  }
}