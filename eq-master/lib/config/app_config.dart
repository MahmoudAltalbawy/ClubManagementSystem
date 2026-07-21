import 'package:flutter/foundation.dart';

class AppConfig {
  const AppConfig._();

  // Configure the backend at launch time instead of committing a developer's LAN IP.
  //
  // Physical Android device:
  //   flutter run --dart-define=API_BASE_URL=http://YOUR_PC_LAN_IP:5122
  //
  // Android emulator:
  //   flutter run --dart-define=USE_ANDROID_EMULATOR=true
  //
  // Optional split form:
  //   flutter run --dart-define=API_HOST=YOUR_PC_LAN_IP --dart-define=API_PORT=5122
  static const String _configuredBaseUrl = String.fromEnvironment(
    'API_BASE_URL',
  );
  static const String _configuredHost = String.fromEnvironment('API_HOST');
  static const int _configuredPort = int.fromEnvironment(
    'API_PORT',
    defaultValue: 5000,
  );
  static const bool _useAndroidEmulator = bool.fromEnvironment(
    'USE_ANDROID_EMULATOR',
    defaultValue: false,
  );

  static String get baseUrl {
    final explicitUrl = _configuredBaseUrl.trim();
    if (explicitUrl.isNotEmpty) {
      return explicitUrl.endsWith('/')
          ? explicitUrl.substring(0, explicitUrl.length - 1)
          : explicitUrl;
    }

    final host = _configuredHost.trim().isNotEmpty
        ? _configuredHost.trim()
        : '34.154.48.159';

    return 'http://$host:$_configuredPort';
  }

  static const String accessTokenKey = 'eq_access_token';
  static const String refreshTokenKey = 'eq_refresh_token';
  static const String userKey = 'eq_user';
  static const String activeTeamKey = 'eq_active_team';
  static const String activeClubKey = 'eq_active_club';
}
