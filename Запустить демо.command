#!/bin/zsh
# Two independent copies of the same application, both starting at the menu.
demo_dir=${0:A:h}
demo_app="$demo_dir/Builds/PredictionLab.app"
if [[ ! -d "$demo_app" ]]; then
  print 'Сначала собери приложение в Unity: Practical 4 → Build macOS and validate simulation'
  read '?Нажми Enter для закрытия'
  exit 1
fi
open -n "$demo_app"
open -n "$demo_app"
