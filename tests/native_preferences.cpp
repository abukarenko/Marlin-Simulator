#include "preferences.h"
#include <iostream>
#include <stdexcept>
int main() {
  const std::string path="artifacts/preferences-test.settings";
  Preferences p; p.set("speed",1.25);p.set("jog_step",5);p.set("panel_Monitor",true);
  if(!p.save(path)) return 1;
  Preferences restored;restored.load(path);
  if(restored.get("speed",0.)!=1.25||restored.get("jog_step",0)!=5||!restored.get("panel_Monitor",false))return 2;
  p.set("speed",2.5);if(!p.save(path))return 3;
  restored.load(path);if(restored.get("speed",0.)!=2.5)return 4;
  restored.values["bad"]="nan";restored.values["invalid"]="1garbage";
  if(restored.get("bad",7.)!=7.||restored.get("invalid",9)!=9)return 5;
  std::filesystem::remove(path);
  std::cout<<"PASS: settings round trip, replacement, invalid values\n";
}
